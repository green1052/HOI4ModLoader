// The profiler's sampling (Profiler.cs starts it and writes the report). Each other thread is sampled in turn, a pass
// about every millisecond: its instruction pointer and its stack, unwound through every loaded module (hoi4.exe, tbb.dll, the
// system DLLs...). While a thread is suspended, nothing here allocates, locks, runs C# or can fault, since the thread may
// hold that lock (a fault's dispatch takes the loader's locks): only its registers and the top of its stack are copied,
// and the copy is unwound once it runs again. Function entries come from each module's own .pdata, not
// RtlLookupFunctionEntry (it takes the loader's inverted-table lock). Unwinding stays in modules with static tables: JIT
// code and hook stubs would need ntdll's locked dynamic tables.
#include <windows.h>
#include <tlhelp32.h>
#include <stdint.h>
#include <string.h>

#define SLOTS (1 << 18) // per table; open addressing, a full table only drops new keys
#define MAX_FRAMES 48
#define MAX_THREADS 256
#define THREADS_EVERY_MS 200
#define MAX_MODULES 512
#define STACK_COPY (128 << 10) // bytes copied from Rsp; a walk stops at the copy's end

typedef struct { uint64_t key; uint32_t count; } Slot;
typedef struct { Slot slots[SLOTS]; uint64_t dropped; } Table;
typedef struct { DWORD id; uint64_t samples, waiting; } ThreadStats;

// Leaf addresses; hoi4.exe function starts on running stacks; main thread wait callers; function starts (any module) on
// the main thread's running stacks.
static Table g_tables[4];
static ThreadStats g_threads[MAX_THREADS];
static int g_threadCount;
static uint64_t g_samples;
static DWORD g_main;
static uint8_t *g_exe, *g_exeEnd, *g_ntdll, *g_ntdllEnd, *g_win32u, *g_win32uEnd;
typedef struct { uint8_t *begin, *end; RUNTIME_FUNCTION* functions; DWORD functionCount; HMODULE pin; } Module;
static Module g_modules[MAX_MODULES]; // loaded modules, listed and pinned when profiling starts
static int g_moduleCount;
static __declspec(align(16)) uint8_t g_stack[STACK_COPY];
static uint64_t g_stackFrom, g_stackSize; // the copied part of the sampled thread's stack

static void Add(Table* table, uint64_t key) {
    size_t i = (key * 0x9E3779B97F4A7C15ull) >> 46;
    for (int probe = 0; probe < 64; i = (i + 1) & (SLOTS - 1), probe++) {
        if (table->slots[i].key == key) { table->slots[i].count++; return; }
        if (table->slots[i].key == 0) { table->slots[i].key = key, table->slots[i].count = 1; return; }
    }
    table->dropped++;
}

static int In(uint64_t at, uint8_t* begin, uint8_t* end) { return at >= (uint64_t)begin && at < (uint64_t)end; }

static Module* ModuleOf(uint64_t at) {
    for (int i = 0; i < g_moduleCount; i++) if (In(at, g_modules[i].begin, g_modules[i].end)) return &g_modules[i];
    return NULL;
}

// RtlLookupFunctionEntry without the lock: a binary search of the module's sorted .pdata.
static RUNTIME_FUNCTION* Lookup(Module* m, uint64_t at) {
    DWORD rva = (DWORD)(at - (uint64_t)m->begin);
    for (DWORD lo = 0, hi = m->functionCount; lo < hi;) {
        DWORD mid = (lo + hi) / 2;
        RUNTIME_FUNCTION* fe = &m->functions[mid];
        if (rva < fe->BeginAddress) hi = mid;
        else if (rva >= fe->EndAddress) lo = mid + 1;
        else return fe->UnwindData & 1 ? (RUNTIME_FUNCTION*)(m->begin + fe->UnwindData - 1) : fe; // indirect entry
    }
    return NULL;
}

static IMAGE_NT_HEADERS* Headers(uint8_t* base) { return (IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew); }

static void ModuleRange(const wchar_t* name, uint8_t** begin, uint8_t** end) {
    *begin = (uint8_t*)GetModuleHandleW(name);
    if (!*begin) { *end = NULL; return; } // not loaded
    *end = *begin + Headers(*begin)->OptionalHeader.SizeOfImage;
}

// While the thread is suspended. ReadProcessMemory fails on a bad address instead of faulting; the copy ends at the
// stack's base, where its committed part ends.
static int CopyStack(CONTEXT* ctx) {
    MEMORY_BASIC_INFORMATION info;
    if (!VirtualQuery((void*)ctx->Rsp, &info, sizeof info) || info.State != MEM_COMMIT || info.Protect != PAGE_READWRITE)
        return 0;
    uint64_t left = (uint64_t)info.BaseAddress + info.RegionSize - ctx->Rsp;
    SIZE_T size = left < STACK_COPY ? left : STACK_COPY, got = 0;
    if (!ReadProcessMemory(GetCurrentProcess(), (void*)ctx->Rsp, g_stack, size, &got) || got < 8) return 0;
    g_stackFrom = ctx->Rsp, g_stackSize = got;
    return 1;
}

// Registers that point into the copied part of the stack (Rsp, a frame pointer, values restored from it) are moved onto
// the copy, so the unwind reads only the copy.
static void Rebase(CONTEXT* ctx) {
    for (DWORD64* r = &ctx->Rax; r <= &ctx->R15; r++)
        if (*r - g_stackFrom < g_stackSize) *r += (uint64_t)g_stack - g_stackFrom;
}

static int OnCopy(uint64_t at) { return at - (uint64_t)g_stack <= g_stackSize - 8; }

static ThreadStats* Stats(DWORD id) {
    for (int i = 0; i < g_threadCount; i++) if (g_threads[i].id == id) return &g_threads[i];
    if (g_threadCount < MAX_THREADS) {
        g_threads[g_threadCount].id = id, g_threads[g_threadCount].samples = 0, g_threads[g_threadCount].waiting = 0;
        return &g_threads[g_threadCount++];
    }
    return &g_threads[MAX_THREADS - 1];
}

// One sample, from the thread's registers and stack copy. The walk stops at frames outside the loaded modules or the copy.
static void Record(DWORD id, CONTEXT* ctx) {
    Rebase(ctx);
    g_samples++;
    ThreadStats* stats = Stats(id);
    stats->samples++;
    uint64_t leaf = ctx->Rip;
    Add(&g_tables[0], leaf);
    // In a system call (wait, sleep, I/O, present): stopped right after a syscall (0F 05) or int 2E (CD 2E) in ntdll
    // or win32u. Other ntdll code, such as the heap, is running.
    uint16_t op = In(leaf, g_ntdll, g_ntdllEnd) || In(leaf, g_win32u, g_win32uEnd) ? *(const uint16_t*)(leaf - 2) : 0;
    int waiting = op == 0x050F || op == 0x2ECD;
    if (waiting) stats->waiting++;
    uint64_t seen[MAX_FRAMES];
    int seenCount = 0, mainWaitDone = 0;
    for (int depth = 0; depth < MAX_FRAMES && ctx->Rip && OnCopy(ctx->Rsp); depth++) {
        Module* m = ModuleOf(ctx->Rip);
        if (!m) break; // JIT code or hook stub
        uint64_t image = (uint64_t)m->begin;
        PRUNTIME_FUNCTION fe = Lookup(m, ctx->Rip);
        if (fe) {
            uint64_t begin = image + fe->BeginAddress;
            int exe = In(ctx->Rip, g_exe, g_exeEnd);
            if (exe && waiting && id == g_main && !mainWaitDone) { Add(&g_tables[2], begin); mainWaitDone = 1; }
            if (!waiting) {
                int dup = 0;
                for (int i = 0; i < seenCount; i++) dup |= seen[i] == begin;
                if (!dup) { // inclusive: once per stack
                    seen[seenCount++] = begin;
                    if (exe) Add(&g_tables[1], begin);
                    if (id == g_main) Add(&g_tables[3], begin);
                }
            }
        }
        if (!fe) { // a leaf function: its return address is on top of the stack
            ctx->Rip = *(uint64_t*)ctx->Rsp;
            ctx->Rsp += 8;
            continue;
        }
        void* handler;
        uint64_t frame;
        RtlVirtualUnwind(UNW_FLAG_NHANDLER, image, ctx->Rip, fe, ctx, &handler, &frame, NULL);
        Rebase(ctx);
    }
}

// A bad stack drops the sample instead of crashing the game. The thread runs again by then, so the fault's dispatch can
// wait on the loader's locks.
static void SafeRecord(DWORD id, CONTEXT* ctx) {
    __try { Record(id, ctx); }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
}

// Samples for ms milliseconds; the tables and thread stats stay until the next call.
void hm_profile(DWORD ms, DWORD mainThread) {
    static HANDLE threads[MAX_THREADS];
    static DWORD ids[MAX_THREADS];
    static __declspec(align(16)) CONTEXT context;
    ModuleRange(NULL, &g_exe, &g_exeEnd);
    ModuleRange(L"ntdll.dll", &g_ntdll, &g_ntdllEnd);
    ModuleRange(L"win32u.dll", &g_win32u, &g_win32uEnd);
    g_moduleCount = 0;
    HANDLE modules = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, 0);
    MODULEENTRY32W me = {sizeof me};
    for (BOOL ok = Module32FirstW(modules, &me); ok && g_moduleCount < MAX_MODULES; ok = Module32NextW(modules, &me)) {
        Module* m = &g_modules[g_moduleCount];
        // Pinned, so its .pdata stays mapped; skipped if it was unloaded since the snapshot.
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, (LPCWSTR)me.modBaseAddr, &m->pin)) continue;
        IMAGE_DATA_DIRECTORY pdata = Headers(me.modBaseAddr)->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXCEPTION];
        m->begin = me.modBaseAddr, m->end = me.modBaseAddr + me.modBaseSize;
        m->functions = (RUNTIME_FUNCTION*)(me.modBaseAddr + pdata.VirtualAddress);
        m->functionCount = pdata.Size / sizeof(RUNTIME_FUNCTION);
        g_moduleCount++;
    }
    CloseHandle(modules);
    memset(g_tables, 0, sizeof g_tables);
    g_threadCount = 0, g_samples = 0, g_main = mainThread;
    int count = 0;
    DWORD self = GetCurrentThreadId(), start = GetTickCount(), listed = start - THREADS_EVERY_MS;
    while (GetTickCount() - start < ms) {
        if (GetTickCount() - listed >= THREADS_EVERY_MS) { // refresh the thread list; nothing is suspended here
            listed = GetTickCount();
            HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
            THREADENTRY32 te = {sizeof te};
            for (BOOL ok = Thread32First(snap, &te); ok && count < MAX_THREADS; ok = Thread32Next(snap, &te)) {
                if (te.th32OwnerProcessID != GetCurrentProcessId() || te.th32ThreadID == self) continue;
                int known = 0;
                for (int i = 0; i < count; i++) known |= ids[i] == te.th32ThreadID;
                if (known) continue;
                HANDLE h = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT, FALSE, te.th32ThreadID);
                if (h) threads[count] = h, ids[count++] = te.th32ThreadID;
            }
            CloseHandle(snap);
        }
        for (int i = 0; i < count; i++) {
            if (SuspendThread(threads[i]) == (DWORD)-1) { // exited: drop it, so a thread reusing the id is reopened
                CloseHandle(threads[i]);
                threads[i] = threads[--count], ids[i] = ids[count];
                i--;
                continue;
            }
            context.ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER;
            int copied = GetThreadContext(threads[i], &context) && CopyStack(&context);
            ResumeThread(threads[i]);
            if (copied) SafeRecord(ids[i], &context);
        }
        Sleep(1);
    }
    for (int i = 0; i < count; i++) CloseHandle(threads[i]);
    for (int i = 0; i < g_moduleCount; i++) FreeLibrary(g_modules[i].pin);
    g_moduleCount = 0;
}

// 0: leaf addresses, 1: hoi4.exe functions on running stacks (inclusive), 2: main thread waits by caller, 3: functions
// of any module on the main thread's running stacks (inclusive). SLOTS entries.
Slot* hm_profile_table(int which, uint64_t* dropped) {
    *dropped = g_tables[which].dropped;
    return g_tables[which].slots;
}

ThreadStats* hm_profile_threads(int* count, uint64_t* samples) {
    *count = g_threadCount;
    *samples = g_samples;
    return g_threads;
}
