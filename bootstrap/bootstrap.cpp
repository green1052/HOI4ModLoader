// version.dll proxy, the only native piece (like BepInEx's Doorstop).
// Forwards the real version.dll exports, and right before hoi4.exe's entry point boots .NET
// and calls HOI4ModLoader.Loader.Init from HOI4ModLoader\core\HOI4ModLoader.dll.
#include <windows.h>
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

static void Fail(const std::wstring& msg) {
    MessageBoxW(nullptr, (g_hostError.empty() ? msg : msg + L"\n\n" + g_hostError).c_str(), L"HOI4ModLoader: mods disabled", MB_ICONWARNING);
}

// An environment variable, or empty (also when it doesn't fit MAX_PATH).
static std::wstring Env(const wchar_t* name) {
    wchar_t value[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(name, value, MAX_PATH);
    return n && n < MAX_PATH ? value : L"";
}

// Where the .NET installer put the x64 runtime (it may not be under Program Files), or empty.
static std::wstring InstallLocation() {
    wchar_t value[MAX_PATH];
    DWORD size = sizeof value;
    return RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64", L"InstallLocation", RRF_RT_REG_SZ | RRF_SUBKEY_WOW6432KEY, nullptr, value, &size) == ERROR_SUCCESS ? value : L"";
}

static bool IsDir(const std::wstring& path) {
    DWORD a = GetFileAttributesW(path.c_str());
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY);
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

    // A runtime shipped in HOI4ModLoader\dotnet wins; otherwise the installed one, found like the official hosts do.
    std::wstring root = mods + L"\\dotnet";
    if (!IsDir(root)) root = Env(L"DOTNET_ROOT");
    if (root.empty()) root = InstallLocation();
    if (root.empty() && !Env(L"ProgramFiles").empty()) root = Env(L"ProgramFiles") + L"\\dotnet";
    std::wstring fxrPath = root.empty() ? L"" : FindHostFxr(root);
    HMODULE fxr = fxrPath.empty() ? nullptr : LoadLibraryW(fxrPath.c_str());
    if (!fxr) return Fail(L".NET 10 runtime not found. Install the .NET 10 Runtime (x64) or use a release that bundles HOI4ModLoader\\dotnet.");

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
