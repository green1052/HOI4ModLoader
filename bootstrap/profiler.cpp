// Start-up profiler: with HOI4ModLoader\profile-startup present, samples where every game thread is (its instruction pointer)
// once a millisecond for the first 150 s and writes HOI4ModLoader\startup-profile.txt: the most sampled addresses, as
// "module+offset count". The game's own load (assets, databases, map) is ~93% of start-up: this says where it goes.
// While a thread is suspended nothing here allocates or takes a lock (it may hold the heap's).
#include <windows.h>
#include <tlhelp32.h>
#include <algorithm>
#include <cstdint>
#include <string>
#include <vector>

#pragma comment(lib, "winmm.lib")

namespace {

constexpr DWORD kDurationMs = 150000, kThreadsEveryMs = 200;
constexpr size_t kSlots = 1 << 20; // open addressing; a full table only drops new addresses

struct Slot { uint64_t at; uint32_t count; };
Slot g_slots[kSlots];               // static: no allocation while sampling
uint64_t g_samples, g_dropped;
std::wstring g_out;
DWORD g_self;

void Count(uint64_t at) {
    g_samples++;
    for (size_t i = (at * 0x9E3779B97F4A7C15ull) >> 44, probe = 0; probe < 64; i = (i + 1) & (kSlots - 1), probe++) {
        if (g_slots[i].at == at) { g_slots[i].count++; return; }
        if (g_slots[i].at == 0) { g_slots[i] = {at, 1}; return; }
    }
    g_dropped++;
}

void Write() {
    std::vector<Slot> hits;
    for (auto& s : g_slots) if (s.at) hits.push_back(s);
    std::sort(hits.begin(), hits.end(), [](const Slot& a, const Slot& b) { return a.count > b.count; });
    std::string text = "samples " + std::to_string(g_samples) + ", dropped " + std::to_string(g_dropped) + "\r\n";
    wchar_t path[MAX_PATH];
    for (size_t i = 0; i < hits.size() && i < 3000; i++) {
        HMODULE m = nullptr;
        char line[MAX_PATH + 64];
        if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)hits[i].at, &m)
            && GetModuleFileNameW(m, path, MAX_PATH)) {
            const wchar_t* name = wcsrchr(path, L'\\');
            name = name ? name + 1 : path;
            char narrow[MAX_PATH];
            WideCharToMultiByte(CP_UTF8, 0, name, -1, narrow, sizeof narrow, nullptr, nullptr);
            sprintf_s(line, "%s+0x%llx %u\r\n", narrow, (unsigned long long)(hits[i].at - (uint64_t)m), hits[i].count);
        } else {
            sprintf_s(line, "0x%llx %u\r\n", (unsigned long long)hits[i].at, hits[i].count);
        }
        text += line;
    }
    HANDLE f = CreateFileW(g_out.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) return;
    DWORD written;
    WriteFile(f, text.data(), (DWORD)text.size(), &written, nullptr);
    CloseHandle(f);
}

DWORD WINAPI Sample(void*) {
    timeBeginPeriod(1);
    std::vector<HANDLE> threads;
    std::vector<DWORD> ids;
    alignas(16) static CONTEXT context;
    DWORD start = GetTickCount(), listed = 0;
    while (GetTickCount() - start < kDurationMs) {
        if (GetTickCount() - listed >= kThreadsEveryMs) { // new threads come and go during load; nothing is suspended here
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
        for (HANDLE h : threads) {
            if (SuspendThread(h) == (DWORD)-1) continue; // exited
            context.ContextFlags = CONTEXT_CONTROL;
            uint64_t at = GetThreadContext(h, &context) ? context.Rip : 0;
            ResumeThread(h);
            if (at) Count(at);
        }
        Sleep(1);
    }
    for (HANDLE h : threads) CloseHandle(h);
    timeEndPeriod(1);
    Write();
    return 0;
}

} // namespace

// Starts the sampler if HOI4ModLoader\profile-startup exists. Called first thing at the exe's entry point.
void StartProfiler(const std::wstring& mods) {
    if (GetFileAttributesW((mods + L"\\profile-startup").c_str()) == INVALID_FILE_ATTRIBUTES) return;
    g_out = mods + L"\\startup-profile.txt";
    HANDLE t = CreateThread(nullptr, 0, Sample, nullptr, 0, &g_self);
    if (t) CloseHandle(t);
}
