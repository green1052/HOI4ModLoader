// The profiler's sampling (Profiler.cs starts it and writes the report). Each other thread is sampled in turn, a pass
// about every millisecond: its instruction pointer and its stack, unwound through every loaded module (hoi4.exe, tbb.dll,
// mimalloc.dll, the system DLLs...). While a thread is suspended, nothing here allocates, locks or runs C#, since the
// thread may hold that lock. Unwinding stays in modules with static tables: JIT code and hook stubs would need ntdll's
// locked dynamic tables.
#include <windows.h>
#include <tlhelp32.h>
#include <stdint.h>
#include <string.h>

#define SLOTS (1 << 18) // per table; open addressing, a full table only drops new keys
#define MAX_FRAMES 48
#define MAX_THREADS 256
#define THREADS_EVERY_MS 200
#define MAX_MODULES 512

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
static uint8_t *g_exe, *g_exeEnd, *g_ntdll, *g_ntdllEnd;
static struct { uint8_t *begin, *end; } g_modules[MAX_MODULES]; // loaded modules, listed when profiling starts
static int g_moduleCount;

static void Add(Table* table, uint64_t key) {
    size_t i = (key * 0x9E3779B97F4A7C15ull) >> 46;
    for (int probe = 0; probe < 64; i = (i + 1) & (SLOTS - 1), probe++) {
        if (table->slots[i].key == key) { table->slots[i].count++; return; }
        if (table->slots[i].key == 0) { table->slots[i].key = key, table->slots[i].count = 1; return; }
    }
    table->dropped++;
}

static int In(uint64_t at, uint8_t* begin, uint8_t* end) { return at >= (uint64_t)begin && at < (uint64_t)end; }

static int InModule(uint64_t at) {
    for (int i = 0; i < g_moduleCount; i++) if (In(at, g_modules[i].begin, g_modules[i].end)) return 1;
    return 0;
}

static void ModuleRange(const wchar_t* name, uint8_t** begin, uint8_t** end) {
    *begin = (uint8_t*)GetModuleHandleW(name);
    IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(*begin + ((IMAGE_DOS_HEADER*)*begin)->e_lfanew);
    *end = *begin + nt->OptionalHeader.SizeOfImage;
}

static ThreadStats* Stats(DWORD id) {
    for (int i = 0; i < g_threadCount; i++) if (g_threads[i].id == id) return &g_threads[i];
    if (g_threadCount < MAX_THREADS) {
        g_threads[g_threadCount].id = id, g_threads[g_threadCount].samples = 0, g_threads[g_threadCount].waiting = 0;
        return &g_threads[g_threadCount++];
    }
    return &g_threads[MAX_THREADS - 1];
}

// One sample of a suspended thread. The walk stops at frames outside the loaded modules.
static void Record(DWORD id, CONTEXT* ctx) {
    g_samples++;
    ThreadStats* stats = Stats(id);
    stats->samples++;
    uint64_t leaf = ctx->Rip;
    Add(&g_tables[0], leaf);
    int waiting = In(leaf, g_ntdll, g_ntdllEnd); // in a system call: wait, sleep or I/O
    if (waiting) stats->waiting++;
    uint64_t seen[MAX_FRAMES];
    int seenCount = 0, mainWaitDone = 0;
    for (int depth = 0; depth < MAX_FRAMES && ctx->Rip; depth++) {
        if (!InModule(ctx->Rip)) break; // JIT code or hook stub
        uint64_t image = 0;
        PRUNTIME_FUNCTION fe = RtlLookupFunctionEntry(ctx->Rip, &image, NULL);
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
    }
}

// A bad stack drops the sample instead of crashing the game.
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
    g_moduleCount = 0;
    HANDLE modules = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, 0);
    MODULEENTRY32W me = {sizeof me};
    for (BOOL ok = Module32FirstW(modules, &me); ok && g_moduleCount < MAX_MODULES; ok = Module32NextW(modules, &me))
        g_modules[g_moduleCount].begin = me.modBaseAddr, g_modules[g_moduleCount++].end = me.modBaseAddr + me.modBaseSize;
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
            if (GetThreadContext(threads[i], &context)) SafeRecord(ids[i], &context);
            ResumeThread(threads[i]);
        }
        Sleep(1);
    }
    for (int i = 0; i < count; i++) CloseHandle(threads[i]);
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
