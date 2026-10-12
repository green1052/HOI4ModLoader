// Profiler. HOI4ModLoader\profile-startup present: the first 150 s from the exe's entry point go to startup-profile.txt;
// afterwards, creating HOI4ModLoader\profile-now profiles the next 60 s (in game, at the speed to look at) into
// profile-<tick>.txt. Every game thread is sampled once a millisecond: its instruction pointer, and its call stack unwound
// through hoi4.exe and the system modules. The report lists, per thread, how much of the time it ran and what it waited in,
// the hoi4.exe functions on the stacks of running samples (inclusive: a function and what it calls), and, for the main
// thread, the hoi4.exe function each wait is called from (what the frame or the load waits for).
// While a thread is suspended nothing here allocates or takes a lock (it may hold the heap's): the unwind lookup only runs on
// addresses inside hoi4.exe and the system modules, whose static tables need none (for JIT code ntdll would search the
// dynamic function tables under their lock, which the suspended thread may hold).
#include <windows.h>
#include <tlhelp32.h>
#include <algorithm>
#include <cstdint>
#include <string>
#include <vector>

#pragma comment(lib, "winmm.lib")

namespace {

constexpr DWORD kStartupMs = 150000, kOnDemandMs = 60000, kThreadsEveryMs = 200;
constexpr size_t kSlots = 1 << 18; // per table; open addressing, a full table only drops new keys
constexpr int kMaxFrames = 48, kMaxThreads = 256;

struct Slot { uint64_t key; uint32_t count; };
struct Table {
    Slot slots[kSlots];
    uint64_t dropped;
    void Add(uint64_t key) {
        for (size_t i = (key * 0x9E3779B97F4A7C15ull) >> 46, probe = 0; probe < 64; i = (i + 1) & (kSlots - 1), probe++) {
            if (slots[i].key == key) { slots[i].count++; return; }
            if (slots[i].key == 0) { slots[i] = {key, 1}; return; }
        }
        dropped++;
    }
    void Clear() { memset(slots, 0, sizeof slots); dropped = 0; }
};

Table g_leaf, g_inclusive, g_mainWaits; // leaf addresses; hoi4.exe function starts on running stacks; main thread wait callers
struct ThreadStats { DWORD id; uint64_t samples, waiting; };
ThreadStats g_threads[kMaxThreads];
int g_threadCount;
uint64_t g_samples;
DWORD g_self, g_main;
std::wstring g_mods;
uint8_t *g_exe, *g_exeEnd, *g_ntdll, *g_ntdllEnd, *g_kernel32, *g_kernel32End, *g_kernelbase, *g_kernelbaseEnd;

bool In(uint64_t at, uint8_t* begin, uint8_t* end) { return at >= (uint64_t)begin && at < (uint64_t)end; }

void ModuleRange(const wchar_t* name, uint8_t*& begin, uint8_t*& end) {
    begin = (uint8_t*)GetModuleHandleW(name);
    auto nt = (IMAGE_NT_HEADERS*)(begin + ((IMAGE_DOS_HEADER*)begin)->e_lfanew);
    end = begin + nt->OptionalHeader.SizeOfImage;
}

ThreadStats& Stats(DWORD id) {
    for (int i = 0; i < g_threadCount; i++) if (g_threads[i].id == id) return g_threads[i];
    if (g_threadCount < kMaxThreads) { g_threads[g_threadCount] = {id, 0, 0}; return g_threads[g_threadCount++]; }
    return g_threads[kMaxThreads - 1];
}

// One sample of a suspended thread: unwinds its stack from the given context (static .pdata only: frames outside hoi4.exe
// and the system modules stop the walk, so no JIT callback runs while the thread is suspended).
void Record(DWORD id, CONTEXT& ctx) {
    g_samples++;
    auto& stats = Stats(id);
    stats.samples++;
    uint64_t leaf = ctx.Rip;
    g_leaf.Add(leaf);
    bool waiting = In(leaf, g_ntdll, g_ntdllEnd); // a system call: a wait, a sleep or file I/O
    if (waiting) stats.waiting++;
    uint64_t seen[kMaxFrames];
    int seenCount = 0;
    bool mainWaitDone = false;
    for (int depth = 0; depth < kMaxFrames && ctx.Rip; depth++) {
        if (!In(ctx.Rip, g_exe, g_exeEnd) && !In(ctx.Rip, g_ntdll, g_ntdllEnd) && !In(ctx.Rip, g_kernel32, g_kernel32End)
            && !In(ctx.Rip, g_kernelbase, g_kernelbaseEnd)) break; // JIT code, a hook stub, another module: stop
        uint64_t image = 0;
        auto fe = RtlLookupFunctionEntry(ctx.Rip, &image, nullptr);
        if (In(ctx.Rip, g_exe, g_exeEnd) && fe) {
            uint64_t begin = image + fe->BeginAddress;
            if (waiting && id == g_main && !mainWaitDone) { g_mainWaits.Add(begin); mainWaitDone = true; }
            if (!waiting && std::find(seen, seen + seenCount, begin) == seen + seenCount) { seen[seenCount++] = begin; g_inclusive.Add(begin); }
        }
        if (!fe) { // a leaf function (system call stubs): its return address is on top of the stack
            if (!In(ctx.Rip, g_ntdll, g_ntdllEnd) && !In(ctx.Rip, g_exe, g_exeEnd)) break; // JIT code, a hook stub: stop
            ctx.Rip = *(uint64_t*)ctx.Rsp;
            ctx.Rsp += 8;
            continue;
        }
        void* handler;
        uint64_t frame;
        RtlVirtualUnwind(UNW_FLAG_NHANDLER, image, ctx.Rip, fe, &ctx, &handler, &frame, nullptr);
    }
}

// A bad stack (a thread mid-switch) must not take the game down: the sample is dropped.
void SafeRecord(DWORD id, CONTEXT& ctx) {
    __try { Record(id, ctx); }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
}

std::string Where(uint64_t at) {
    HMODULE m = nullptr;
    wchar_t path[MAX_PATH];
    char line[MAX_PATH + 32];
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)at, &m) && GetModuleFileNameW(m, path, MAX_PATH)) {
        const wchar_t* name = wcsrchr(path, L'\\');
        char narrow[MAX_PATH];
        WideCharToMultiByte(CP_UTF8, 0, name ? name + 1 : path, -1, narrow, sizeof narrow, nullptr, nullptr);
        sprintf_s(line, "%s+0x%llx", narrow, (unsigned long long)(at - (uint64_t)m));
    } else sprintf_s(line, "0x%llx", (unsigned long long)at);
    return line;
}

void Section(std::string& text, const char* title, Table& table, size_t top) {
    std::vector<Slot> hits;
    for (auto& s : table.slots) if (s.key) hits.push_back(s);
    std::sort(hits.begin(), hits.end(), [](const Slot& a, const Slot& b) { return a.count > b.count; });
    text += std::string("== ") + title + " (dropped " + std::to_string(table.dropped) + ")\r\n";
    for (size_t i = 0; i < hits.size() && i < top; i++) text += Where(hits[i].key) + " " + std::to_string(hits[i].count) + "\r\n";
}

void Write(const std::wstring& out, DWORD ms) {
    std::string text = "samples " + std::to_string(g_samples) + " over " + std::to_string(ms) + " ms, about 1 per thread per ms\r\n";
    std::sort(g_threads, g_threads + g_threadCount, [](const ThreadStats& a, const ThreadStats& b) { return a.samples - a.waiting > b.samples - b.waiting; });
    text += "== threads (id: running / sampled)\r\n";
    for (int i = 0; i < g_threadCount; i++)
        text += std::to_string(g_threads[i].id) + (g_threads[i].id == g_main ? " (main)" : "") + ": "
              + std::to_string(g_threads[i].samples - g_threads[i].waiting) + " / " + std::to_string(g_threads[i].samples) + "\r\n";
    Section(text, "hoi4.exe functions on running stacks (inclusive)", g_inclusive, 400);
    Section(text, "main thread waits, by the hoi4.exe function they are called from", g_mainWaits, 100);
    Section(text, "leaf addresses", g_leaf, 300);
    HANDLE f = CreateFileW(out.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) return;
    DWORD written;
    WriteFile(f, text.data(), (DWORD)text.size(), &written, nullptr);
    CloseHandle(f);
}

void Profile(DWORD ms, const std::wstring& out) {
    timeBeginPeriod(1); // 1 ms samples; the game keeps its own timer resolution otherwise
    g_leaf.Clear(), g_inclusive.Clear(), g_mainWaits.Clear();
    g_threadCount = 0, g_samples = 0;
    std::vector<HANDLE> threads;
    std::vector<DWORD> ids;
    alignas(16) static CONTEXT context;
    DWORD start = GetTickCount(), listed = 0;
    while (GetTickCount() - start < ms) {
        if (GetTickCount() - listed >= kThreadsEveryMs) { // threads come and go; nothing is suspended here
            listed = GetTickCount();
            HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
            THREADENTRY32 te{sizeof te};
            for (BOOL ok = Thread32First(snap, &te); ok; ok = Thread32Next(snap, &te)) {
                if (te.th32OwnerProcessID != GetCurrentProcessId() || te.th32ThreadID == g_self) continue;
                if (std::find(ids.begin(), ids.end(), te.th32ThreadID) != ids.end()) continue;
                HANDLE h = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT, FALSE, te.th32ThreadID);
                if (h) { threads.push_back(h); ids.push_back(te.th32ThreadID); }
            }
            CloseHandle(snap);
        }
        for (size_t i = 0; i < threads.size(); i++) {
            if (SuspendThread(threads[i]) == (DWORD)-1) continue; // exited
            context.ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER;
            if (GetThreadContext(threads[i], &context)) SafeRecord(ids[i], context);
            ResumeThread(threads[i]);
        }
        Sleep(1);
    }
    for (HANDLE h : threads) CloseHandle(h);
    timeEndPeriod(1);
    Write(out, ms);
}

DWORD WINAPI Run(void* startup) {
    ModuleRange(nullptr, g_exe, g_exeEnd);
    ModuleRange(L"ntdll.dll", g_ntdll, g_ntdllEnd);
    ModuleRange(L"kernel32.dll", g_kernel32, g_kernel32End);
    ModuleRange(L"kernelbase.dll", g_kernelbase, g_kernelbaseEnd);
    if (startup) Profile(kStartupMs, g_mods + L"\\startup-profile.txt");
    for (std::wstring trigger = g_mods + L"\\profile-now";; Sleep(1000)) // on demand, for the rest of the run
        if (DeleteFileW(trigger.c_str())) Profile(kOnDemandMs, g_mods + L"\\profile-" + std::to_wstring(GetTickCount64()) + L".txt");
}

} // namespace

// Called first thing at the exe's entry point, on the main thread.
void StartProfiler(const std::wstring& mods) {
    g_mods = mods;
    g_main = GetCurrentThreadId();
    bool startup = GetFileAttributesW((mods + L"\\profile-startup").c_str()) != INVALID_FILE_ATTRIBUTES;
    HANDLE t = CreateThread(nullptr, 0, Run, startup ? (void*)1 : nullptr, 0, &g_self);
    if (t) CloseHandle(t);
}
