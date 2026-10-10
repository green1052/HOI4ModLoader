// mimalloc in place of hoi4.exe's statically linked CRT heap. The C# loader (Allocator.cs) patches the game's
// _malloc_base, _calloc_base, _realloc_base, _free_base and _msize to jump here, before the game's CRT starts.
// Blocks that didn't come from mimalloc (anything on the process heap handed over by another module) still go to
// the CRT heap, so freeing them works as before.
#include <windows.h>
#include <string.h>
#include <mimalloc.h>

static HANDLE* g_crtHeap; // the game's CRT heap handle variable, filled by its CRT startup

__declspec(dllexport) void hm_set_crt_heap(HANDLE* heap) { g_crtHeap = heap; }

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
