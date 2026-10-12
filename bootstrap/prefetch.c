// The prefetch's file recorder (Prefetch.cs does the rest): hoi4.exe's import of CreateFileW goes through here, and the
// files it opens for reading are appended to a list. Native: it runs on whatever game thread opens a file.
#include <windows.h>
#include <string.h>

#define RECORD_CAP (64u << 20) // bytes; past it nothing more is recorded

typedef HANDLE(WINAPI* CreateFileWFn)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
static CreateFileWFn g_createFile;
static SRWLOCK g_lock = SRWLOCK_INIT;
static int g_recording;
static wchar_t* g_list; // "path\n" per open, in order, repeats included; on the process heap
static size_t g_length, g_capacity; // in wchar_t

static HANDLE WINAPI RecordingCreateFileW(LPCWSTR name, DWORD access, DWORD share, LPSECURITY_ATTRIBUTES sa, DWORD disposition, DWORD flags, HANDLE templ) {
    HANDLE h = g_createFile(name, access, share, sa, disposition, flags, templ);
    if (h == INVALID_HANDLE_VALUE || !name || !g_recording || !(access & GENERIC_READ) || disposition != OPEN_EXISTING
        || (flags & FILE_FLAG_BACKUP_SEMANTICS)) return h; // a file read, not a directory or a new file
    size_t n = wcslen(name) + 1;
    AcquireSRWLockExclusive(&g_lock);
    if (g_recording && g_length + n > g_capacity && (g_capacity + n) * 2 * sizeof(wchar_t) <= RECORD_CAP) {
        size_t capacity = (g_capacity + n) * 2;
        wchar_t* grown = g_list ? HeapReAlloc(GetProcessHeap(), 0, g_list, capacity * sizeof(wchar_t)) : HeapAlloc(GetProcessHeap(), 0, capacity * sizeof(wchar_t));
        if (grown) g_list = grown, g_capacity = capacity;
    }
    if (g_recording && g_length + n <= g_capacity) {
        memcpy(g_list + g_length, name, (n - 1) * sizeof(wchar_t));
        g_list[g_length + n - 1] = L'\n';
        g_length += n;
    }
    ReleaseSRWLockExclusive(&g_lock);
    return h;
}

// Points hoi4.exe's import of kernel32's CreateFileW at the recorder; 0 if it has none.
int hm_record_start(void) {
    BYTE* base = (BYTE*)GetModuleHandleW(NULL);
    IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew);
    IMAGE_DATA_DIRECTORY* dir = &nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    void* real = (void*)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "CreateFileW");
    for (IMAGE_IMPORT_DESCRIPTOR* imp = (IMAGE_IMPORT_DESCRIPTOR*)(base + dir->VirtualAddress); imp->Name; imp++) {
        for (IMAGE_THUNK_DATA* thunk = (IMAGE_THUNK_DATA*)(base + imp->FirstThunk); thunk->u1.Function; thunk++) {
            if ((void*)thunk->u1.Function != real) continue;
            DWORD old;
            VirtualProtect(&thunk->u1.Function, sizeof(void*), PAGE_READWRITE, &old);
            g_createFile = (CreateFileWFn)thunk->u1.Function;
            g_recording = 1;
            thunk->u1.Function = (ULONGLONG)&RecordingCreateFileW;
            VirtualProtect(&thunk->u1.Function, sizeof(void*), old, &old);
            return 1;
        }
    }
    return 0;
}

// Stops recording and hands the list over (the caller frees it with hm_record_free); the hook stays, passing calls through.
wchar_t* hm_record_stop(size_t* length) {
    AcquireSRWLockExclusive(&g_lock);
    g_recording = 0;
    wchar_t* list = g_list;
    *length = g_length;
    g_list = NULL, g_length = g_capacity = 0;
    ReleaseSRWLockExclusive(&g_lock);
    return list;
}

void hm_record_free(wchar_t* list) {
    if (list) HeapFree(GetProcessHeap(), 0, list);
}
