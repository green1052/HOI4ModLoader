// Start-up prefetch. A cold start of the game is mostly waiting on the disk (24 s warm against 62 s cold; the start-up
// profile shows its threads idle in waits, little CPU): so each start records the files the game opens for reading in its
// first two minutes (HOI4ModLoader\cache\prefetch.txt), and the next start reads that list, in order, on four threads
// right at the entry point, into the system's file cache before the game asks for them. HOI4ModLoader\no-prefetch turns
// both off.
#include <windows.h>
#include <atomic>
#include <string>
#include <unordered_set>
#include <vector>

namespace {

constexpr DWORD kRecordMs = 120000, kPrefetchMs = 90000;
constexpr int kReaders = 4;

using CreateFileWFn = HANDLE(WINAPI*)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
CreateFileWFn g_createFile;
CRITICAL_SECTION g_lock;
std::vector<std::wstring> g_opened;   // in first-open order
std::unordered_set<std::wstring> g_seen;
std::atomic<bool> g_recording;
std::wstring g_list;

std::vector<std::wstring> g_prefetch;
std::atomic<size_t> g_next;
DWORD g_prefetchStart;

HANDLE WINAPI RecordingCreateFileW(LPCWSTR name, DWORD access, DWORD share, LPSECURITY_ATTRIBUTES sa, DWORD disposition, DWORD flags, HANDLE templ) {
    HANDLE h = g_createFile(name, access, share, sa, disposition, flags, templ);
    if (h != INVALID_HANDLE_VALUE && name && g_recording.load(std::memory_order_relaxed) && (access & GENERIC_READ)
        && disposition == OPEN_EXISTING && !(flags & FILE_FLAG_BACKUP_SEMANTICS)) { // a file read, not a directory or a new file
        EnterCriticalSection(&g_lock);
        if (g_seen.insert(name).second) g_opened.emplace_back(name);
        LeaveCriticalSection(&g_lock);
    }
    return h;
}

// Points hoi4.exe's import of kernel32's CreateFileW at the recorder.
bool HookImport() {
    auto base = (uint8_t*)GetModuleHandleW(nullptr);
    auto nt = (IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew);
    auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    void* real = (void*)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "CreateFileW");
    for (auto imp = (IMAGE_IMPORT_DESCRIPTOR*)(base + dir.VirtualAddress); imp->Name; imp++) {
        for (auto thunk = (IMAGE_THUNK_DATA*)(base + imp->FirstThunk); thunk->u1.Function; thunk++) {
            if ((void*)thunk->u1.Function != real) continue;
            DWORD old;
            VirtualProtect(&thunk->u1.Function, sizeof(void*), PAGE_READWRITE, &old);
            g_createFile = (CreateFileWFn)thunk->u1.Function;
            thunk->u1.Function = (ULONGLONG)&RecordingCreateFileW;
            VirtualProtect(&thunk->u1.Function, sizeof(void*), old, &old);
            return true;
        }
    }
    return false;
}

DWORD WINAPI SaveList(void*) {
    Sleep(kRecordMs);
    g_recording = false;
    EnterCriticalSection(&g_lock);
    std::wstring text;
    for (auto& p : g_opened) text += p + L"\n";
    LeaveCriticalSection(&g_lock);
    if (text.empty()) return 0;
    int n = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int)text.size(), nullptr, 0, nullptr, nullptr);
    std::string utf8(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int)text.size(), utf8.data(), n, nullptr, nullptr);
    std::wstring tmp = g_list + L".tmp";
    HANDLE f = CreateFileW(tmp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) return 0;
    DWORD written;
    WriteFile(f, utf8.data(), (DWORD)utf8.size(), &written, nullptr);
    CloseHandle(f);
    MoveFileExW(tmp.c_str(), g_list.c_str(), MOVEFILE_REPLACE_EXISTING);
    return 0;
}

DWORD WINAPI Read(void*) {
    static thread_local char buffer[1 << 20];
    for (size_t i; (i = g_next.fetch_add(1)) < g_prefetch.size() && GetTickCount() - g_prefetchStart < kPrefetchMs;) {
        HANDLE f = CreateFileW(g_prefetch[i].c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                               OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
        if (f == INVALID_HANDLE_VALUE) continue;
        DWORD got;
        while (ReadFile(f, buffer, sizeof buffer, &got, nullptr) && got) {}
        CloseHandle(f);
    }
    return 0;
}

void LoadList() {
    HANDLE f = CreateFileW(g_list.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) return;
    LARGE_INTEGER size;
    GetFileSizeEx(f, &size);
    std::string utf8((size_t)size.QuadPart, '\0');
    DWORD got = 0;
    ReadFile(f, utf8.data(), (DWORD)utf8.size(), &got, nullptr);
    CloseHandle(f);
    int n = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), (int)got, nullptr, 0);
    std::wstring text(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8.data(), (int)got, text.data(), n);
    for (size_t at = 0, nl; (nl = text.find(L'\n', at)) != std::wstring::npos; at = nl + 1)
        if (nl > at) g_prefetch.emplace_back(text.substr(at, nl - at));
}

} // namespace

// Called first thing at the exe's entry point, before the game reads anything.
void StartPrefetch(const std::wstring& mods) {
    if (GetFileAttributesW((mods + L"\\no-prefetch").c_str()) != INVALID_FILE_ATTRIBUTES) return;
    CreateDirectoryW((mods + L"\\cache").c_str(), nullptr);
    g_list = mods + L"\\cache\\prefetch.txt";
    LoadList();
    if (!g_prefetch.empty()) {
        g_prefetchStart = GetTickCount();
        for (int i = 0; i < kReaders; i++)
            if (HANDLE t = CreateThread(nullptr, 0, Read, nullptr, 0, nullptr)) CloseHandle(t);
    }
    InitializeCriticalSection(&g_lock);
    if (!HookImport()) return;
    g_recording = true;
    if (HANDLE t = CreateThread(nullptr, 0, SaveList, nullptr, 0, nullptr)) CloseHandle(t);
}
