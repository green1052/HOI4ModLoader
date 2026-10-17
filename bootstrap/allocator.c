// mimalloc in place of hoi4.exe's CRT heap. Allocator.cs loads mimalloc.dll, passes its functions to hm_init, and points
// the game's _malloc_base, _calloc_base, _realloc_base, _free_base and _msize here. Native, since every allocation runs it.
// Blocks not from mimalloc still go to the CRT heap.
#include <windows.h>
#include <string.h>

static HANDLE* g_crtHeap; // the game's CRT heap handle variable, filled by its CRT startup
static void* (*mi_malloc)(size_t);
static void* (*mi_calloc)(size_t, size_t);
static void* (*mi_realloc)(void*, size_t);
static void (*mi_free)(void*);
static size_t (*mi_usable_size)(const void*);
static unsigned char (*mi_is_in_heap_region)(const void*);
static DWORD g_ringFls = FLS_OUT_OF_INDEXES; // the quarantine rings' FLS slot, see hm_free
static void WINAPI flushRing(void* data);

__declspec(dllexport) void hm_init(HANDLE* crtHeap, void* malloc_, void* calloc_, void* realloc_, void* free_, void* usableSize, void* isInHeap) {
    g_crtHeap = crtHeap;
    mi_malloc = malloc_;
    mi_calloc = calloc_;
    mi_realloc = realloc_;
    mi_free = free_;
    mi_usable_size = usableSize;
    mi_is_in_heap_region = isInHeap;
    g_ringFls = FlsAlloc(flushRing);
}

__declspec(dllexport) void* hm_malloc(size_t size) { return mi_malloc(size); }

__declspec(dllexport) void* hm_calloc(size_t count, size_t size) { return mi_calloc(count, size); }

static void release(void* p) {
    if (mi_is_in_heap_region(p)) mi_free(p);
    else HeapFree(*g_crtHeap, 0, p);
}

// Deferred frees, for game code that reads objects right after freeing them. Between hm_defer(1) and the outermost
// hm_defer(0), a thread's frees are queued (on the process heap, capped; past the cap they happen at once).
#define DEFER_CAP (1 << 20)
static __declspec(thread) int t_deferDepth;
static __declspec(thread) void** t_deferred;
static __declspec(thread) size_t t_deferredCount, t_deferredCapacity;

static int queue(void* p) {
    if (t_deferredCount == t_deferredCapacity) {
        if (t_deferredCapacity >= DEFER_CAP) return 0;
        size_t capacity = t_deferredCapacity ? t_deferredCapacity * 2 : 256;
        void** grown = t_deferred ? HeapReAlloc(GetProcessHeap(), 0, t_deferred, capacity * sizeof(void*))
                                  : HeapAlloc(GetProcessHeap(), 0, capacity * sizeof(void*));
        if (!grown) return 0;
        t_deferred = grown;
        t_deferredCapacity = capacity;
    }
    t_deferred[t_deferredCount++] = p;
    return 1;
}

__declspec(dllexport) void hm_defer(int on) {
    if (on) {
        t_deferDepth++;
        return;
    }
    if (t_deferDepth == 0 || --t_deferDepth > 0) return;
    for (size_t i = 0; i < t_deferredCount; i++) release(t_deferred[i]);
    t_deferredCount = 0;
}

// Quarantine: a small block is freed only after QUARANTINE more small frees on the same thread, so the game's
// use-after-free reads still find the old contents. Per thread; flushed by the FLS callback on thread exit.
// ponytail: catches reads within ~16k frees; a later one still crashes (wrap that call in hm_defer like ActivateDecision).
#define QUARANTINE (1 << 14)
#define QUARANTINE_MAX_SIZE 4096
typedef struct { size_t next; void* slots[QUARANTINE]; } Ring;
static __declspec(thread) Ring* t_ring;
static __declspec(thread) int t_noRing; // no ring (flushed or failed): free at once

static void WINAPI flushRing(void* data) {
    Ring* ring = data;
    if (!ring) return;
    t_ring = NULL;
    t_noRing = 1;
    for (size_t i = 0; i < QUARANTINE; i++)
        if (ring->slots[i]) mi_free(ring->slots[i]);
    HeapFree(GetProcessHeap(), 0, ring);
}

static Ring* threadRing(void) {
    if (t_ring || t_noRing) return t_ring;
    Ring* ring = g_ringFls == FLS_OUT_OF_INDEXES ? NULL : HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(Ring));
    if (ring && !FlsSetValue(g_ringFls, ring)) {
        HeapFree(GetProcessHeap(), 0, ring);
        ring = NULL;
    }
    t_noRing = !ring;
    return t_ring = ring;
}

__declspec(dllexport) void hm_free(void* p) {
    if (!p) return;
    if (t_deferDepth > 0 && queue(p)) return;
    if (!mi_is_in_heap_region(p)) {
        HeapFree(*g_crtHeap, 0, p);
        return;
    }
    Ring* ring = mi_usable_size(p) <= QUARANTINE_MAX_SIZE ? threadRing() : NULL;
    if (ring) {
        void** slot = &ring->slots[ring->next++ & (QUARANTINE - 1)];
        void* oldest = *slot;
        *slot = p;
        if (!oldest) return;
        p = oldest;
    }
    mi_free(p);
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
    void* moved = mi_malloc(size); // move a CRT block to mimalloc
    if (!moved) return NULL;
    size_t old = HeapSize(*g_crtHeap, 0, p);
    memcpy(moved, p, old < size ? old : size);
    HeapFree(*g_crtHeap, 0, p);
    return moved;
}
