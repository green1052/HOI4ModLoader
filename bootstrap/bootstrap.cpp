// version.dll proxy, the only native piece (like BepInEx's Doorstop).
// Forwards the real version.dll exports, and right before hoi4.exe's entry point boots .NET
// and calls HOI4ModLoader.Loader.Init from HOI4ModLoader\core\HOI4ModLoader.dll.
#include <windows.h>
#include <shlobj.h>
#include <cstdint>
#include <string>

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

// The few hostfxr declarations we need (hostfxr.h / coreclr_delegates.h), so building needs no .NET SDK packs.
struct hostfxr_initialize_parameters { size_t size; const wchar_t* host_path; const wchar_t* dotnet_root; };
using hostfxr_initialize_for_runtime_config_fn = int(__cdecl*)(const wchar_t*, const hostfxr_initialize_parameters*, void**);
using hostfxr_get_runtime_delegate_fn = int(__cdecl*)(void*, int, void**);
using hostfxr_close_fn = int(__cdecl*)(void*);
using hostfxr_error_writer_fn = void(__cdecl*)(const wchar_t*);
using hostfxr_set_error_writer_fn = hostfxr_error_writer_fn(__cdecl*)(hostfxr_error_writer_fn);
using load_assembly_and_get_function_pointer_fn = int(__stdcall*)(const wchar_t*, const wchar_t*, const wchar_t*, const wchar_t*, void*, void**);
constexpr int hdt_load_assembly_and_get_function_pointer = 5;
const wchar_t* const UNMANAGEDCALLERSONLY_METHOD = (const wchar_t*)-1;

static uint8_t* g_entry;
static uint8_t g_saved[12];
static std::wstring g_hostError;  // what hostfxr reported (it writes to stderr, which the game doesn't have)

// ---- Crash diagnostics: HOI4ModLoader\crash.log ----
// Managed hooks and timers mark what they are on their thread (hm_enter/hm_leave: a static label). A vectored handler,
// registered after .NET's own, logs native faults on marked threads and faults outside every module (hook stubs, asm
// caves), then lets the exception go on: the game's crash reporter and .NET's fail-fast run exactly as before. .NET turns
// faults in managed code into its own exceptions first; its fatal errors ("Fatal error.", "Unhandled exception.",
// "Stack overflow.", with the managed stack) go to stderr, which points at the same file.
static thread_local const char* t_crumb;
static thread_local bool t_reporting;
static HANDLE g_crashLog = INVALID_HANDLE_VALUE;
static volatile LONG g_crashLock;
static LONG g_reports;
static char g_line[1024]; // static, not stack
static size_t g_len;
static wchar_t g_module[MAX_PATH];

extern "C" __declspec(dllexport) const char* hm_enter(const char* label) {
    const char* previous = t_crumb;
    t_crumb = label;
    return previous;
}

extern "C" __declspec(dllexport) void hm_leave(const char* previous) { t_crumb = previous; }

// Formatting without printf or the heap: the faulting thread may hold the heap lock.
static void Put(const char* s) { while (*s && g_len < sizeof g_line - 2) g_line[g_len++] = *s++; }

static void PutNum(uint64_t v, unsigned base, int width) {
    char t[24];
    int i = 24;
    do { t[--i] = "0123456789ABCDEF"[v % base]; v /= base; } while (v || 24 - i < width);
    while (i < 24 && g_len < sizeof g_line - 2) g_line[g_len++] = t[i++];
}

static void PutHex(uint64_t v) { Put("0x"); PutNum(v, 16, 1); }

static void PutTime() {
    SYSTEMTIME t;
    GetLocalTime(&t);
    PutNum(t.wHour, 10, 2); Put(":"); PutNum(t.wMinute, 10, 2); Put(":"); PutNum(t.wSecond, 10, 2); Put("."); PutNum(t.wMilliseconds, 10, 3);
}

static HMODULE ModuleOf(const void* at) {
    HMODULE m = nullptr;
    return GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)at, &m) ? m : nullptr;
}

// "hoi4.exe+0x1A2B3C", or the bare address outside every module.
static void PutWhere(const void* at) {
    HMODULE m = ModuleOf(at);
    if (!m || !GetModuleFileNameW(m, g_module, MAX_PATH)) {
        PutHex((uint64_t)at);
        Put(" (no module: hook stub, asm cave or JIT code)");
        return;
    }
    const wchar_t* name = wcsrchr(g_module, L'\\');
    for (name = name ? name + 1 : g_module; *name && g_len < sizeof g_line - 2; name++) g_line[g_len++] = *name < 128 ? (char)*name : '?';
    Put("+");
    PutHex((uint64_t)((const uint8_t*)at - (const uint8_t*)m));
}

static void Flush() {
    g_line[g_len++] = '\r';
    g_line[g_len++] = '\n';
    DWORD written;
    WriteFile(g_crashLog, g_line, (DWORD)g_len, &written, nullptr); // an append handle: one write, one line
    g_len = 0;
}

static LONG CALLBACK OnFault(EXCEPTION_POINTERS* e) {
    const EXCEPTION_RECORD* r = e->ExceptionRecord;
    DWORD code = r->ExceptionCode;
    // No stack left to run on: a second overflow here would kill the process before the game's crash report. .NET prints
    // "Stack overflow." (to crash.log) for every thread that ever ran managed code.
    if (code == EXCEPTION_STACK_OVERFLOW) return EXCEPTION_CONTINUE_SEARCH;
    // Errors (C0000005 access violation, C000001D illegal instruction...) and int3. Not C++ (E06D7363) or .NET (E0434352)
    // throws, which their own handlers catch.
    if ((code & 0xF0000000) != 0xC0000000 && code != EXCEPTION_BREAKPOINT) return EXCEPTION_CONTINUE_SEARCH;
    if (t_reporting || g_reports >= 32) return EXCEPTION_CONTINUE_SEARCH; // a fault while reporting, or a loop of handled ones
    const char* crumb = t_crumb;
    if (!crumb && ModuleOf(r->ExceptionAddress)) return EXCEPTION_CONTINUE_SEARCH; // the game's or Windows' own: the game's crash report covers it
    t_reporting = true;
    while (InterlockedExchange(&g_crashLock, 1)) SwitchToThread();
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
        if (kind == 8 && sp >= low && sp + 8 <= high) { // jumped or called somewhere not executable: [rsp] is the caller if it was a call
            Put(", return address ");
            PutWhere(*(void**)sp); // only within this thread's stack: a fault here would make the game's report blame us
        }
    }
    Put(", in ");
    Put(crumb ? crumb : "no loader or plugin code");
    Flush();
    InterlockedExchange(&g_crashLock, 0);
    t_reporting = false;
    return EXCEPTION_CONTINUE_SEARCH;
}

// Kept across starts (a crash must survive the restart), deleted once it passes 1 MB.
static void OpenCrashLog(const std::wstring& mods) {
    std::wstring path = mods + L"\\crash.log";
    WIN32_FILE_ATTRIBUTE_DATA a;
    if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &a) && (a.nFileSizeHigh || a.nFileSizeLow > (1u << 20))) DeleteFileW(path.c_str());
    g_crashLog = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (g_crashLog == INVALID_HANDLE_VALUE) return;
    SetStdHandle(STD_ERROR_HANDLE, g_crashLog); // before .NET starts (Console.Error caches it): its fatal error text and managed stack go to stderr
    Put("---- ");
    PutTime();
    Put(" hoi4.exe started, pid ");
    PutNum(GetCurrentProcessId(), 10, 1);
    Flush();
}

static void Fail(const std::wstring& msg) {
    MessageBoxW(nullptr, (g_hostError.empty() ? msg : msg + L"\n\n" + g_hostError).c_str(), L"HOI4ModLoader: mods disabled", MB_ICONWARNING);
}


// <root>\host\fxr\<highest version>\hostfxr.dll, or empty.
static std::wstring FindHostFxr(const std::wstring& root) {
    std::wstring best, bestDir;
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((root + L"\\host\\fxr\\*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return L"";
    auto key = [](const std::wstring& v) {  // "10.0.12" -> zero-padded "00010.00000.00012" for comparison
        std::wstring k, part;
        for (wchar_t c : v + L".") {
            if (c != L'.') { part += c; continue; }
            k += std::wstring(5 - min(part.size(), (size_t)5), L'0') + part + L".";
            part.clear();
        }
        return k;
    };
    do {
        if (fd.cFileName[0] != L'.' && (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) && key(fd.cFileName) > best) {
            best = key(fd.cFileName);
            bestDir = fd.cFileName;
        }
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    return bestDir.empty() ? L"" : root + L"\\host\\fxr\\" + bestDir + L"\\hostfxr.dll";
}

static void BootDotnet() {
    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    std::wstring mods = std::wstring(exe, wcsrchr(exe, L'\\')) + L"\\HOI4ModLoader";
    OpenCrashLog(mods);

    // The .NET 10 Runtime (x64) the player installed, where its installer puts it.
    wchar_t programFiles[MAX_PATH];
    std::wstring root = SHGetSpecialFolderPathW(nullptr, programFiles, CSIDL_PROGRAM_FILES, FALSE) ? std::wstring(programFiles) + L"\\dotnet" : L"";
    std::wstring fxrPath = root.empty() ? L"" : FindHostFxr(root);
    HMODULE fxr = fxrPath.empty() ? nullptr : LoadLibraryW(fxrPath.c_str());
    if (!fxr) return Fail(L".NET 10 Runtime (x64) not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and start the game again.");

    auto init = (hostfxr_initialize_for_runtime_config_fn)GetProcAddress(fxr, "hostfxr_initialize_for_runtime_config");
    auto getDelegate = (hostfxr_get_runtime_delegate_fn)GetProcAddress(fxr, "hostfxr_get_runtime_delegate");
    auto close = (hostfxr_close_fn)GetProcAddress(fxr, "hostfxr_close");
    auto setErrorWriter = (hostfxr_set_error_writer_fn)GetProcAddress(fxr, "hostfxr_set_error_writer");
    if (!init || !getDelegate || !close) return Fail(fxrPath + L" is too old for .NET 10.");
    if (setErrorWriter) setErrorWriter([](const wchar_t* message) { g_hostError += message, g_hostError += L"\n"; });

    std::wstring core = mods + L"\\core\\";
    hostfxr_initialize_parameters params{sizeof(params), exe, root.c_str()};
    void* h = nullptr;
    if (init((core + L"HOI4ModLoader.runtimeconfig.json").c_str(), &params, &h) < 0 || !h)
        return Fail(L"Failed to start .NET with " + core + L"HOI4ModLoader.runtimeconfig.json (is .NET 10 installed?)");
    load_assembly_and_get_function_pointer_fn load = nullptr;
    int got = getDelegate(h, hdt_load_assembly_and_get_function_pointer, (void**)&load);
    close(h);
    if (got < 0) load = nullptr;

    void(__stdcall * loaderInit)() = nullptr;
    if (!load || load((core + L"HOI4ModLoader.dll").c_str(), L"HOI4ModLoader.Loader, HOI4ModLoader", L"Init", UNMANAGEDCALLERSONLY_METHOD, nullptr, (void**)&loaderInit) != 0 || !loaderInit)
        return Fail(L"Failed to load HOI4ModLoader.Loader.Init from " + core + L"HOI4ModLoader.dll");
    if (g_crashLog != INVALID_HANDLE_VALUE) AddVectoredExceptionHandler(0, OnFault); // last in line: .NET's (added when it started) handles managed-code faults first
    loaderInit();
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
    BootDotnet();
    return ((DWORD(WINAPI*)(void*))g_entry)(peb);
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(module);
    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    // The launcher and crash reporter live in the same folder and load this proxy too.
    if (_wcsicmp(wcsrchr(exe, L'\\') + 1, L"hoi4.exe")) return TRUE;

    auto base = (uint8_t*)GetModuleHandleW(nullptr);
    auto nt = (IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew);
    g_entry = base + nt->OptionalHeader.AddressOfEntryPoint;
    memcpy(g_saved, g_entry, sizeof g_saved);
    uint8_t jmp[12] = {0x48, 0xB8};  // mov rax, EntryHook; jmp rax
    auto target = (uint64_t)&EntryHook;
    memcpy(jmp + 2, &target, 8);
    jmp[10] = 0xFF, jmp[11] = 0xE0;
    Write(g_entry, jmp, sizeof jmp);
    return TRUE;
}
