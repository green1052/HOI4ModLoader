// mimalloc in place of hoi4.exe's statically linked CRT heap. The C# loader (Allocator.cs) loads mimalloc.dll, hands its
// functions to hm_init, and patches the game's _malloc_base, _calloc_base, _realloc_base, _free_base and _msize to jump
// here, before the game's CRT starts. These run on every allocation, so they stay native (no managed transition).
// Blocks that didn't come from mimalloc (anything on the process heap handed over by another module) still go to the
// CRT heap, so freeing them works as before.
#include <windows.h>
#include <string.h>

static HANDLE* g_crtHeap; // the game's CRT heap handle variable, filled by its CRT startup
static void* (*mi_malloc)(size_t);
static void* (*mi_calloc)(size_t, size_t);
static void* (*mi_realloc)(void*, size_t);
static void (*mi_free)(void*);
static size_t (*mi_usable_size)(const void*);
static unsigned char (*mi_is_in_heap_region)(const void*);

__declspec(dllexport) void hm_init(HANDLE* crtHeap, void* malloc_, void* calloc_, void* realloc_, void* free_, void* usableSize, void* isInHeap) {
    g_crtHeap = crtHeap;
    mi_malloc = malloc_;
    mi_calloc = calloc_;
    mi_realloc = realloc_;
    mi_free = free_;
    mi_usable_size = usableSize;
    mi_is_in_heap_region = isInHeap;
}

__declspec(dllexport) void* hm_malloc(size_t size) { return mi_malloc(size); }

__declspec(dllexport) void* hm_calloc(size_t count, size_t size) { return mi_calloc(count, size); }

__declspec(dllexport) void hm_free(void* p) {
    if (!p) return;
    if (mi_is_in_heap_region(p)) mi_free(p);
    else HeapFree(*g_crtHeap, 0, p);
}

__declspec(dllexport) size_t hm_msize(void* p) {
    if (!p) return (size_t)-1;
    return mi_is_in_heap_region(p) ? mi_usable_size(p) : HeapSize(*g_crtHeap, 0, p);
}

__declspec(dllexport) void* hm_realloc(void* p, size_t size) {
    if (!p) return mi_malloc(size);
    if (size == 0) { // the CRT frees and returns null
        hm_free(p);
        return NULL;
    }
    if (mi_is_in_heap_region(p)) return mi_realloc(p, size);
    void* moved = mi_malloc(size); // a CRT heap block moves over to mimalloc
    if (!moved) return NULL;
    size_t old = HeapSize(*g_crtHeap, 0, p);
    memcpy(moved, p, old < size ? old : size);
    HeapFree(*g_crtHeap, 0, p);
    return moved;
}
