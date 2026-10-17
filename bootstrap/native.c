// The native half of version.dll (Boot.cs is the rest): export forwarders, the entry point hook and the crash handler.
// C, because this runs on game threads at any moment, where calling into .NET could wait on its garbage collector.
#include <windows.h>
#include <stdint.h>

#define FWD(name) __pragma(comment(linker, "/export:" #name "=\\\\.\\GLOBALROOT\\SystemRoot\\System32\\version." #name))
FWD(GetFileVersionInfoA)
FWD(GetFileVersionInfoByHandle)
FWD(GetFileVersionInfoExA)
FWD(GetFileVersionInfoExW)
FWD(GetFileVersionInfoSizeA)
FWD(GetFileVersionInfoSizeExA)
FWD(GetFileVersionInfoSizeExW)
FWD(GetFileVersionInfoSizeW)
FWD(GetFileVersionInfoW)
FWD(VerFindFileA)
FWD(VerFindFileW)
FWD(VerInstallFileA)
FWD(VerInstallFileW)
FWD(VerLanguageNameA)
FWD(VerLanguageNameW)
FWD(VerQueryValueA)
FWD(VerQueryValueW)

void* hm_boot(void); // Boot.cs: starts everything and returns HOI4ModLoader.Loader.Init, or null

static uint8_t* g_entry;
static uint8_t g_saved[12];

// ---- Crash diagnostics: HOI4ModLoader\crash.log ----
// Hooks and timers label their thread (hm_enter/hm_leave). A vectored handler, after .NET's, logs native faults on
// labelled threads and outside every module (hook stubs, asm caves), then passes the exception on unchanged.
// .NET's fatal errors go to stderr, which Boot.cs points at the same file.
static __declspec(thread) const char* t_crumb;
static __declspec(thread) int t_reporting;
static HANDLE g_crashLog = INVALID_HANDLE_VALUE;
static volatile LONG g_crashLock;
static LONG g_reports;
static char g_line[1024]; // static, not stack
static size_t g_len;
static wchar_t g_module[MAX_PATH];

__declspec(dllexport) const char* hm_enter(const char* label) {
    const char* previous = t_crumb;
    t_crumb = label;
    return previous;
}

__declspec(dllexport) void hm_leave(const char* previous) { t_crumb = previous; }

// The file Boot.cs opened; faults are logged once .NET has started.
void hm_crash_log(HANDLE file) { g_crashLog = file; }

// Formatting without printf or the heap: the faulting thread may hold the heap lock.
static void Put(const char* s) { while (*s && g_len < sizeof g_line - 2) g_line[g_len++] = *s++; }

static void PutNum(uint64_t v, unsigned base, int width) {
    char t[24];
    int i = 24;
    do { t[--i] = "0123456789ABCDEF"[v % base]; v /= base; } while (v || 24 - i < width);
    while (i < 24 && g_len < sizeof g_line - 2) g_line[g_len++] = t[i++];
}

static void PutHex(uint64_t v) { Put("0x"); PutNum(v, 16, 1); }

static void PutTime(void) {
    SYSTEMTIME t;
    GetLocalTime(&t);
    PutNum(t.wHour, 10, 2); Put(":"); PutNum(t.wMinute, 10, 2); Put(":"); PutNum(t.wSecond, 10, 2); Put("."); PutNum(t.wMilliseconds, 10, 3);
}

static HMODULE ModuleOf(const void* at) {
    HMODULE m = NULL;
    return GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)at, &m) ? m : NULL;
}

// "hoi4.exe+0x1A2B3C", or the bare address outside every module.
static void PutWhere(const void* at) {
    HMODULE m = ModuleOf(at);
    if (!m || !GetModuleFileNameW(m, g_module, MAX_PATH)) {
        PutHex((uint64_t)at);
        Put(" (no module: hook stub, asm cave or JIT code)");
        return;
    }
    const wchar_t* name = g_module;
    for (const wchar_t* c = g_module; *c; c++) if (*c == L'\\') name = c + 1;
    for (; *name && g_len < sizeof g_line - 2; name++) g_line[g_len++] = *name < 128 ? (char)*name : '?';
    Put("+");
    PutHex((uint64_t)((const uint8_t*)at - (const uint8_t*)m));
}

static void Flush(void) {
    g_line[g_len++] = '\r';
    g_line[g_len++] = '\n';
    DWORD written;
    WriteFile(g_crashLog, g_line, (DWORD)g_len, &written, NULL); // an append handle: one write, one line
    g_len = 0;
}

static LONG CALLBACK OnFault(EXCEPTION_POINTERS* e) {
    const EXCEPTION_RECORD* r = e->ExceptionRecord;
    DWORD code = r->ExceptionCode;
    // No stack left: a second overflow here would kill the process before the game's crash report.
    if (code == EXCEPTION_STACK_OVERFLOW) return EXCEPTION_CONTINUE_SEARCH;
    // Errors (C0000005, C000001D...) and int3; not C++ (E06D7363) or .NET (E0434352) throws.
    if ((code & 0xF0000000) != 0xC0000000 && code != EXCEPTION_BREAKPOINT) return EXCEPTION_CONTINUE_SEARCH;
    if (t_reporting || g_reports >= 32) return EXCEPTION_CONTINUE_SEARCH; // a fault while reporting, or a loop of handled ones
    const char* crumb = t_crumb;
    if (!crumb && ModuleOf(r->ExceptionAddress)) return EXCEPTION_CONTINUE_SEARCH; // the game's crash report covers it
    t_reporting = 1;
    // Bounded wait: the holder may need a lock this thread holds. Unreported beats hung.
    for (int waited = 0; InterlockedExchange(&g_crashLock, 1); waited++) {
        if (waited == 1000) {
            t_reporting = 0;
            return EXCEPTION_CONTINUE_SEARCH;
        }
        Sleep(1);
    }
    g_reports++;
    PutTime();
    Put(" FAULT (first chance) thread ");
    PutNum(GetCurrentThreadId(), 10, 1);
    Put(": ");
    PutNum(code, 16, 8);
    Put(" at ");
    PutWhere(r->ExceptionAddress);
    if ((code == EXCEPTION_ACCESS_VIOLATION || code == EXCEPTION_IN_PAGE_ERROR) && r->NumberParameters >= 2) {
        ULONG_PTR kind = r->ExceptionInformation[0];
        Put(kind == 0 ? " reading " : kind == 1 ? " writing " : " executing ");
        PutHex(r->ExceptionInformation[1]);
        ULONG_PTR low, high, sp = e->ContextRecord->Rsp;
        GetCurrentThreadStackLimits(&low, &high);
        if (kind == 8 && sp >= low && sp + 8 <= high) { // executing bad memory: [rsp] is the caller if it was a call
            Put(", return address ");
            PutWhere(*(void**)sp);
        }
    }
    Put(", in ");
    Put(crumb ? crumb : "no loader or plugin code");
    Flush();
    InterlockedExchange(&g_crashLock, 0);
    t_reporting = 0;
    return EXCEPTION_CONTINUE_SEARCH;
}

static void Write(void* at, const void* data, size_t n) {
    DWORD old;
    VirtualProtect(at, n, PAGE_EXECUTE_READWRITE, &old);
    memcpy(at, data, n);
    VirtualProtect(at, n, old, &old);
    FlushInstructionCache(GetCurrentProcess(), at, n);
}

// Runs in place of the exe entry point (outside the loader lock): unhook, boot mods, continue.
static DWORD WINAPI EntryHook(void* peb) {
    Write(g_entry, g_saved, sizeof g_saved);
    void (*init)(void) = (void (*)(void))hm_boot();
    if (init) {
        if (g_crashLog != INVALID_HANDLE_VALUE) AddVectoredExceptionHandler(0, OnFault); // after .NET's, which handles managed faults
        init();
    }
    return ((DWORD(WINAPI*)(void*))g_entry)(peb);
}

// The DLL's entry point (/ENTRY): runs the CRT's (and NativeAOT's DllMain), then hooks the exe's entry point.
BOOL WINAPI _DllMainCRTStartup(HINSTANCE module, DWORD reason, LPVOID reserved);

BOOL WINAPI hm_entry(HINSTANCE module, DWORD reason, LPVOID reserved) {
    BOOL ok = _DllMainCRTStartup(module, reason, reserved);
    if (!ok || reason != DLL_PROCESS_ATTACH) return ok;
    wchar_t exe[MAX_PATH];
    const wchar_t* name = exe;
    GetModuleFileNameW(NULL, exe, MAX_PATH);
    for (const wchar_t* c = exe; *c; c++) if (*c == L'\\') name = c + 1;
    // The launcher and crash reporter load this proxy too.
    if (_wcsicmp(name, L"hoi4.exe")) return TRUE;

    uint8_t* base = (uint8_t*)GetModuleHandleW(NULL);
    IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew);
    g_entry = base + nt->OptionalHeader.AddressOfEntryPoint;
    memcpy(g_saved, g_entry, sizeof g_saved);
    uint8_t jmp[12] = {0x48, 0xB8}; // mov rax, EntryHook; jmp rax
    uint64_t target = (uint64_t)&EntryHook;
    memcpy(jmp + 2, &target, 8);
    jmp[10] = 0xFF, jmp[11] = 0xE0;
    Write(g_entry, jmp, sizeof jmp);
    return TRUE;
}
