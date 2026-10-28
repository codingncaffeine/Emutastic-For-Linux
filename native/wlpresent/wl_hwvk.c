// Vulkan hardware rendering for libretro cores (RETRO_HW_CONTEXT_VULKAN): the frontend side.
//
// The core makes its device on our instance through its context negotiation interface (we add the external-memory
// and external-semaphore extensions to it) and hands each frame over as a VkImage (set_image +
// RETRO_HW_FRAME_BUFFER_VALID). wlvk_frame copies that image on the GPU into one of four exportable slots; the
// present thread imports each slot's memory and semaphores into its GL context once (GL_EXT_memory_object_fd,
// GL_EXT_semaphore_fd) and draws it straight from there: no frame crosses the CPU. A slot's "ready" semaphore is
// signalled by Vulkan after the copy and waited by GL before the first draw of it; its "done" semaphore is signalled
// by GL when the slot is no longer shown and waited by Vulkan before the slot is written again. A slot written but
// replaced before it was ever shown is STALE: GL still takes its "ready" (balancing the binary semaphore) and gives
// "done" back. Where GL cannot import (no extension, another GPU), wlvk_readback hands the frame to the CPU path.
#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>
#include <libretro.h>
#include <libretro_vulkan.h>

#include <EGL/egl.h>
#include <GL/gl.h>
#include <GL/glext.h>
#include <dlfcn.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#include "wl_present.h"

// ── Vulkan entry points ───────────────────────────────────────────────────────────────────────────────────────────
#define VK_INSTANCE_FNS(X) \
    X(vkDestroyInstance) X(vkEnumeratePhysicalDevices) X(vkGetPhysicalDeviceProperties) X(vkGetPhysicalDeviceProperties2) \
    X(vkGetPhysicalDeviceQueueFamilyProperties) X(vkGetPhysicalDeviceMemoryProperties) X(vkCreateDevice) X(vkGetDeviceProcAddr) \
    X(vkEnumerateDeviceExtensionProperties)
#define VK_DEVICE_FNS(X) \
    X(vkDestroyDevice) X(vkGetDeviceQueue) X(vkCreateCommandPool) X(vkDestroyCommandPool) X(vkAllocateCommandBuffers) \
    X(vkBeginCommandBuffer) X(vkEndCommandBuffer) X(vkCmdPipelineBarrier) X(vkCmdBlitImage) X(vkCmdCopyImageToBuffer) \
    X(vkQueueSubmit) X(vkQueueWaitIdle) X(vkCreateFence) X(vkDestroyFence) X(vkWaitForFences) X(vkResetFences) \
    X(vkCreateImage) X(vkDestroyImage) X(vkGetImageMemoryRequirements) X(vkAllocateMemory) X(vkFreeMemory) \
    X(vkBindImageMemory) X(vkCreateSemaphore) X(vkDestroySemaphore) X(vkCreateBuffer) X(vkDestroyBuffer) \
    X(vkGetBufferMemoryRequirements) X(vkBindBufferMemory) X(vkMapMemory) X(vkUnmapMemory) X(vkDeviceWaitIdle)

#define WLVK_SLOTS 4
enum { SLOT_FREE, SLOT_READY, SLOT_SHOWN, SLOT_STALE };

typedef struct {
    VkImage img;
    VkDeviceMemory mem;
    VkDeviceSize memsize;
    int w, h;
    int gen;              // bumps whenever the image is made again (GL imports anew)
    VkSemaphore ready, done;
    int state;
    int done_pending;     // GL signalled "done": the next write waits for it
    int external;         // released to the external queue family (written at least once)
    VkCommandBuffer cb;
    VkFence fence;
    int fence_pending;
    uint64_t seq;
    // GL side (present thread only)
    GLuint gl_tex, gl_mem, gl_ready, gl_done;
    int gl_gen;           // the gen the GL texture was imported at (-1: none)
    int gl_sems;          // the semaphores are imported
} wlvk_slot;

typedef struct {
    void *lib;
    PFN_vkGetInstanceProcAddr gipa;
    PFN_vkCreateInstance vkCreateInstance;
#define DECL(n) PFN_##n n;
    VK_INSTANCE_FNS(DECL)
    VK_DEVICE_FNS(DECL)
#undef DECL
    PFN_vkGetMemoryFdKHR vkGetMemoryFdKHR;
    PFN_vkGetSemaphoreFdKHR vkGetSemaphoreFdKHR;
    VkInstance inst;
    VkPhysicalDevice gpu;
    VkDevice dev;
    VkQueue queue;
    uint32_t family;
    int own_device;                 // we made it (no negotiation): we destroy it
    int interop;                    // the device has the external memory/semaphore extensions
    uint8_t device_uuid[VK_UUID_SIZE], driver_uuid[VK_UUID_SIZE];
    VkPhysicalDeviceMemoryProperties memprops;
    pthread_mutex_t qlock;          // the queue, shared with the core (lock_queue)
    pthread_mutex_t slock;          // slot states
    struct retro_hw_render_interface_vulkan iface;
    const struct retro_hw_render_context_negotiation_interface_vulkan *neg;
    // the core's sync indices: one fence each (an empty submission after the frame's copy)
    VkFence sync_fence[2];
    int sync_pending[2];
    unsigned sync_index;
    VkImage handed;                 // set_image's, for this frame
    VkCommandPool pool;
    wlvk_slot slots[WLVK_SLOTS];
    uint64_t seq;
    int last_slot;                  // the newest written slot (readback)
    // readback (CPU fallback)
    VkBuffer rb_buf;
    VkDeviceMemory rb_mem;
    VkDeviceSize rb_size;
    void *rb_map;
    VkCommandBuffer rb_cb;
    VkFence rb_fence;
    // GL entry points and state (present thread)
    int gl_checked, gl_ok;
    int shown;                      // the slot the window shows (-1: none yet)
} wlvk;

static const char *kExtMemFd = VK_KHR_EXTERNAL_MEMORY_FD_EXTENSION_NAME;
static const char *kExtSemFd = VK_KHR_EXTERNAL_SEMAPHORE_FD_EXTENSION_NAME;
static wlvk *g_vk;  // the one context (the libretro callbacks carry a handle too)

static void vk_log(const char *msg) { fprintf(stderr, "[wlvk] %s\n", msg); }

static uint32_t memory_type(wlvk *v, uint32_t bits, VkMemoryPropertyFlags want) {
    for (uint32_t i = 0; i < v->memprops.memoryTypeCount; ++i)
        if ((bits & (1u << i)) && (v->memprops.memoryTypes[i].propertyFlags & want) == want) return i;
    return UINT32_MAX;
}

static int has_extension(wlvk *v, VkPhysicalDevice gpu, const char *name) {
    uint32_t n = 0;
    v->vkEnumerateDeviceExtensionProperties(gpu, NULL, &n, NULL);
    VkExtensionProperties *p = calloc(n ? n : 1, sizeof *p);
    v->vkEnumerateDeviceExtensionProperties(gpu, NULL, &n, p);
    int found = 0;
    for (uint32_t i = 0; i < n && !found; ++i) found = strcmp(p[i].extensionName, name) == 0;
    free(p);
    return found;
}

// ── the interface the core calls ──────────────────────────────────────────────────────────────────────────────────
static void cb_lock(void *h) { pthread_mutex_lock(&((wlvk *)h)->qlock); }
static void cb_unlock(void *h) { pthread_mutex_unlock(&((wlvk *)h)->qlock); }
static void cb_set_image(void *h, const struct retro_vulkan_image *image, uint32_t n, const VkSemaphore *s, uint32_t q) {
    (void)n; (void)s; (void)q;
    ((wlvk *)h)->handed = image ? image->create_info.image : VK_NULL_HANDLE;
}
static uint32_t cb_get_sync_index(void *h) { return ((wlvk *)h)->sync_index; }
static uint32_t cb_get_sync_index_mask(void *h) { (void)h; return 3u; }
static void cb_set_command_buffers(void *h, uint32_t n, const VkCommandBuffer *cbs) { (void)h; (void)n; (void)cbs; }
static void wait_index(wlvk *v, unsigned i) {
    if (!v->sync_pending[i]) return;
    v->vkWaitForFences(v->dev, 1, &v->sync_fence[i], VK_TRUE, UINT64_MAX);
    v->vkResetFences(v->dev, 1, &v->sync_fence[i]);
    v->sync_pending[i] = 0;
}
static void cb_wait_sync_index(void *h) { wlvk *v = h; wait_index(v, v->sync_index); }
static void cb_set_signal_semaphore(void *h, VkSemaphore s) { (void)h; (void)s; }

// The core's device creation goes through here: our extensions are added to its list.
static VkDevice device_wrapper(VkPhysicalDevice gpu, void *opaque, const VkDeviceCreateInfo *ci) {
    wlvk *v = opaque;
    const char **ext = malloc(sizeof(char *) * (ci->enabledExtensionCount + 2));
    uint32_t n = 0;
    for (uint32_t i = 0; i < ci->enabledExtensionCount; ++i) ext[n++] = ci->ppEnabledExtensionNames[i];
    v->interop = has_extension(v, gpu, kExtMemFd) && has_extension(v, gpu, kExtSemFd);
    if (v->interop) {
        int have_mem = 0, have_sem = 0;
        for (uint32_t i = 0; i < n; ++i) { have_mem |= strcmp(ext[i], kExtMemFd) == 0; have_sem |= strcmp(ext[i], kExtSemFd) == 0; }
        if (!have_mem) ext[n++] = kExtMemFd;
        if (!have_sem) ext[n++] = kExtSemFd;
    }
    VkDeviceCreateInfo with = *ci;
    with.enabledExtensionCount = n;
    with.ppEnabledExtensionNames = ext;
    VkDevice d = VK_NULL_HANDLE;
    if (v->vkCreateDevice(gpu, &with, NULL, &d) != VK_SUCCESS) d = VK_NULL_HANDLE;
    free(ext);
    return d;
}

static VkPhysicalDevice first_gpu(wlvk *v) {
    uint32_t n = 0;
    v->vkEnumeratePhysicalDevices(v->inst, &n, NULL);
    if (!n) return VK_NULL_HANDLE;
    VkPhysicalDevice *g = calloc(n, sizeof *g);
    v->vkEnumeratePhysicalDevices(v->inst, &n, g);
    VkPhysicalDevice best = g[0];
    int best_score = -1;
    for (uint32_t i = 0; i < n; ++i) {
        VkPhysicalDeviceProperties p;
        v->vkGetPhysicalDeviceProperties(g[i], &p);
        int score = p.deviceType == VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU ? 3 : p.deviceType == VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU ? 2 : 1;
        if (score > best_score) { best_score = score; best = g[i]; }
    }
    free(g);
    return best;
}

// Without the core's negotiation: one graphics + compute queue and our extensions.
static int own_device(wlvk *v, struct retro_vulkan_context *ctx) {
    VkPhysicalDevice gpu = first_gpu(v);
    if (!gpu) return 0;
    uint32_t nf = 0;
    v->vkGetPhysicalDeviceQueueFamilyProperties(gpu, &nf, NULL);
    VkQueueFamilyProperties *qf = calloc(nf ? nf : 1, sizeof *qf);
    v->vkGetPhysicalDeviceQueueFamilyProperties(gpu, &nf, qf);
    uint32_t fam = UINT32_MAX;
    for (uint32_t i = 0; i < nf && fam == UINT32_MAX; ++i)
        if ((qf[i].queueFlags & (VK_QUEUE_GRAPHICS_BIT | VK_QUEUE_COMPUTE_BIT)) == (VK_QUEUE_GRAPHICS_BIT | VK_QUEUE_COMPUTE_BIT)) fam = i;
    free(qf);
    if (fam == UINT32_MAX) return 0;
    float prio = 1.0f;
    VkDeviceQueueCreateInfo qci = {VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO, NULL, 0, fam, 1, &prio};
    VkDeviceCreateInfo ci = {VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO};
    ci.queueCreateInfoCount = 1;
    ci.pQueueCreateInfos = &qci;
    VkDevice d = device_wrapper(gpu, v, &ci);
    if (!d) return 0;
    PFN_vkGetDeviceQueue gq = (PFN_vkGetDeviceQueue)v->vkGetDeviceProcAddr(d, "vkGetDeviceQueue");
    ctx->gpu = gpu;
    ctx->device = d;
    ctx->queue_family_index = fam;
    gq(d, fam, 0, &ctx->queue);
    v->own_device = 1;
    return 1;
}

void *wlvk_create(const void *negotiation) {
    wlvk *v = calloc(1, sizeof *v);
    if (!v) return NULL;
    v->neg = negotiation;
    v->shown = -1;
    v->last_slot = -1;
    pthread_mutex_init(&v->qlock, NULL);
    pthread_mutex_init(&v->slock, NULL);
    v->lib = dlopen("libvulkan.so.1", RTLD_NOW | RTLD_LOCAL);
    if (!v->lib) v->lib = dlopen("libvulkan.so", RTLD_NOW | RTLD_LOCAL);
    if (!v->lib) { vk_log("no Vulkan loader"); free(v); return NULL; }
    v->gipa = (PFN_vkGetInstanceProcAddr)dlsym(v->lib, "vkGetInstanceProcAddr");
    v->vkCreateInstance = (PFN_vkCreateInstance)v->gipa(NULL, "vkCreateInstance");
    VkApplicationInfo fallback = {VK_STRUCTURE_TYPE_APPLICATION_INFO, NULL, "Emutastic", 0, "Emutastic", 0, VK_API_VERSION_1_1};
    const VkApplicationInfo *app = (v->neg && v->neg->get_application_info) ? v->neg->get_application_info() : NULL;
    VkApplicationInfo use = app ? *app : fallback;
    if (use.apiVersion < VK_API_VERSION_1_1) use.apiVersion = VK_API_VERSION_1_1;  // external memory, ID properties
    VkInstanceCreateInfo ici = {VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};
    ici.pApplicationInfo = &use;
    if (!v->vkCreateInstance || v->vkCreateInstance(&ici, NULL, &v->inst) != VK_SUCCESS) { vk_log("vkCreateInstance failed"); dlclose(v->lib); free(v); return NULL; }
#define LOADI(n) v->n = (PFN_##n)v->gipa(v->inst, #n);
    VK_INSTANCE_FNS(LOADI)
#undef LOADI
    struct retro_vulkan_context ctx;
    memset(&ctx, 0, sizeof ctx);
    int ok = 0;
    if (v->neg && v->neg->interface_version >= 2 && v->neg->create_device2)
        ok = v->neg->create_device2(&ctx, v->inst, VK_NULL_HANDLE, VK_NULL_HANDLE, v->gipa, device_wrapper, v);
    else if (v->neg && v->neg->create_device) {
        // v1: our extensions as the frontend's required ones (when the GPU it picks has them).
        VkPhysicalDevice gpu = first_gpu(v);
        v->interop = gpu && has_extension(v, gpu, kExtMemFd) && has_extension(v, gpu, kExtSemFd);
        const char *req[2] = {kExtMemFd, kExtSemFd};
        ok = v->neg->create_device(&ctx, v->inst, VK_NULL_HANDLE, VK_NULL_HANDLE, v->gipa, v->interop ? req : NULL, v->interop ? 2 : 0, NULL, 0, NULL);
    }
    if (!ok) ok = own_device(v, &ctx);
    if (!ok) { vk_log("no device"); v->vkDestroyInstance(v->inst, NULL); dlclose(v->lib); free(v); return NULL; }
    v->gpu = ctx.gpu;
    v->dev = ctx.device;
    v->queue = ctx.queue;
    v->family = ctx.queue_family_index;
#define LOADD(n) v->n = (PFN_##n)v->vkGetDeviceProcAddr(v->dev, #n);
    VK_DEVICE_FNS(LOADD)
#undef LOADD
    if (v->interop) {
        v->vkGetMemoryFdKHR = (PFN_vkGetMemoryFdKHR)v->vkGetDeviceProcAddr(v->dev, "vkGetMemoryFdKHR");
        v->vkGetSemaphoreFdKHR = (PFN_vkGetSemaphoreFdKHR)v->vkGetDeviceProcAddr(v->dev, "vkGetSemaphoreFdKHR");
        v->interop = v->vkGetMemoryFdKHR && v->vkGetSemaphoreFdKHR;
    }
    v->vkGetPhysicalDeviceMemoryProperties(v->gpu, &v->memprops);
    VkPhysicalDeviceIDProperties idp = {VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES};
    VkPhysicalDeviceProperties2 p2 = {VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2, &idp};
    v->vkGetPhysicalDeviceProperties2(v->gpu, &p2);
    memcpy(v->device_uuid, idp.deviceUUID, VK_UUID_SIZE);
    memcpy(v->driver_uuid, idp.driverUUID, VK_UUID_SIZE);
    VkCommandPoolCreateInfo pci = {VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO, NULL, VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT, v->family};
    v->vkCreateCommandPool(v->dev, &pci, NULL, &v->pool);
    VkFenceCreateInfo fci = {VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
    for (int i = 0; i < 2; ++i) v->vkCreateFence(v->dev, &fci, NULL, &v->sync_fence[i]);
    for (int i = 0; i < WLVK_SLOTS; ++i) {
        wlvk_slot *s = &v->slots[i];
        VkCommandBufferAllocateInfo cai = {VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO, NULL, v->pool, VK_COMMAND_BUFFER_LEVEL_PRIMARY, 1};
        v->vkAllocateCommandBuffers(v->dev, &cai, &s->cb);
        v->vkCreateFence(v->dev, &fci, NULL, &s->fence);
        s->gl_gen = -1;
        if (v->interop) {
            VkExportSemaphoreCreateInfo exp = {VK_STRUCTURE_TYPE_EXPORT_SEMAPHORE_CREATE_INFO, NULL, VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_OPAQUE_FD_BIT};
            VkSemaphoreCreateInfo sci = {VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO, &exp};
            v->vkCreateSemaphore(v->dev, &sci, NULL, &s->ready);
            v->vkCreateSemaphore(v->dev, &sci, NULL, &s->done);
        }
    }
    {
        VkCommandBufferAllocateInfo cai = {VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO, NULL, v->pool, VK_COMMAND_BUFFER_LEVEL_PRIMARY, 1};
        v->vkAllocateCommandBuffers(v->dev, &cai, &v->rb_cb);
        v->vkCreateFence(v->dev, &fci, NULL, &v->rb_fence);
    }
    struct retro_hw_render_interface_vulkan *f = &v->iface;
    f->interface_type = RETRO_HW_RENDER_INTERFACE_VULKAN;
    f->interface_version = RETRO_HW_RENDER_INTERFACE_VULKAN_VERSION;
    f->handle = v;
    f->instance = v->inst;
    f->gpu = v->gpu;
    f->device = v->dev;
    f->get_device_proc_addr = v->vkGetDeviceProcAddr;
    f->get_instance_proc_addr = v->gipa;
    f->queue = v->queue;
    f->queue_index = v->family;
    f->set_image = cb_set_image;
    f->get_sync_index = cb_get_sync_index;
    f->get_sync_index_mask = cb_get_sync_index_mask;
    f->set_command_buffers = cb_set_command_buffers;
    f->wait_sync_index = cb_wait_sync_index;
    f->lock_queue = cb_lock;
    f->unlock_queue = cb_unlock;
    f->set_signal_semaphore = cb_set_signal_semaphore;
    VkPhysicalDeviceProperties props;
    v->vkGetPhysicalDeviceProperties(v->gpu, &props);
    fprintf(stderr, "[wlvk] device %s, frames to GL %s\n", props.deviceName, v->interop ? "on the GPU" : "through the CPU");
    g_vk = v;
    return v;
}

const void *wlvk_interface(void *h) { return h ? &((wlvk *)h)->iface : NULL; }

static void destroy_slot_image(wlvk *v, wlvk_slot *s) {
    if (s->img) v->vkDestroyImage(v->dev, s->img, NULL);
    if (s->mem) v->vkFreeMemory(v->dev, s->mem, NULL);
    s->img = VK_NULL_HANDLE;
    s->mem = VK_NULL_HANDLE;
    s->external = 0;
    s->w = s->h = 0;
}

static int make_slot_image(wlvk *v, wlvk_slot *s, int w, int h) {
    VkExternalMemoryImageCreateInfo ext = {VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO, NULL, VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_FD_BIT};
    VkImageCreateInfo ici = {VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
    ici.pNext = v->interop ? &ext : NULL;
    ici.imageType = VK_IMAGE_TYPE_2D;
    ici.format = VK_FORMAT_R8G8B8A8_UNORM;  // GL_RGBA8 on the other side
    ici.extent.width = (uint32_t)w;
    ici.extent.height = (uint32_t)h;
    ici.extent.depth = 1;
    ici.mipLevels = 1;
    ici.arrayLayers = 1;
    ici.samples = VK_SAMPLE_COUNT_1_BIT;
    ici.tiling = VK_IMAGE_TILING_OPTIMAL;
    ici.usage = VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_SAMPLED_BIT;
    ici.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    if (v->vkCreateImage(v->dev, &ici, NULL, &s->img) != VK_SUCCESS) return 0;
    VkMemoryRequirements req;
    v->vkGetImageMemoryRequirements(v->dev, s->img, &req);
    VkMemoryDedicatedAllocateInfo ded = {VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO, NULL, s->img, VK_NULL_HANDLE};
    VkExportMemoryAllocateInfo exp = {VK_STRUCTURE_TYPE_EXPORT_MEMORY_ALLOCATE_INFO, &ded, VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_FD_BIT};
    VkMemoryAllocateInfo mai = {VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO, v->interop ? (void *)&exp : NULL, req.size,
                                memory_type(v, req.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT)};
    if (mai.memoryTypeIndex == UINT32_MAX || v->vkAllocateMemory(v->dev, &mai, NULL, &s->mem) != VK_SUCCESS) { destroy_slot_image(v, s); return 0; }
    v->vkBindImageMemory(v->dev, s->img, s->mem, 0);
    s->memsize = req.size;
    s->w = w;
    s->h = h;
    s->gen++;
    return 1;
}

static void submit(wlvk *v, const VkSubmitInfo *si, VkFence fence) {
    pthread_mutex_lock(&v->qlock);
    v->vkQueueSubmit(v->queue, si ? 1 : 0, si, fence);
    pthread_mutex_unlock(&v->qlock);
}

// The handed-over image (this frame's set_image) into a slot for the present thread. Returns 1 when a frame went in.
int wlvk_frame(void *h, int w, int hh) {
    wlvk *v = h;
    if (!v || !v->handed || w <= 0 || hh <= 0) return 0;
    pthread_mutex_lock(&v->slock);
    int pick = -1;
    for (int i = 0; i < WLVK_SLOTS; ++i)
        if (v->slots[i].state == SLOT_FREE && (pick < 0 || v->slots[i].seq < v->slots[pick].seq)) pick = i;
    if (pick < 0) { pthread_mutex_unlock(&v->slock); return 0; }  // the window has not caught up: drop this one
    pthread_mutex_unlock(&v->slock);
    wlvk_slot *s = &v->slots[pick];
    if (s->fence_pending) {
        v->vkWaitForFences(v->dev, 1, &s->fence, VK_TRUE, UINT64_MAX);
        v->vkResetFences(v->dev, 1, &s->fence);
        s->fence_pending = 0;
    }
    if (s->img && (s->w != w || s->h != hh)) destroy_slot_image(v, s);
    if (!s->img && !make_slot_image(v, s, w, hh)) return 0;
    VkCommandBufferBeginInfo bi = {VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO, NULL, VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT};
    v->vkBeginCommandBuffer(s->cb, &bi);
    VkImageMemoryBarrier b[2];
    memset(b, 0, sizeof b);
    // The slot back from GL (or new), ready to be written.
    b[0].sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    b[0].srcAccessMask = 0;
    b[0].dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    b[0].oldLayout = s->external ? VK_IMAGE_LAYOUT_GENERAL : VK_IMAGE_LAYOUT_UNDEFINED;
    b[0].newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    b[0].srcQueueFamilyIndex = s->external ? VK_QUEUE_FAMILY_EXTERNAL : VK_QUEUE_FAMILY_IGNORED;
    b[0].dstQueueFamilyIndex = s->external ? v->family : VK_QUEUE_FAMILY_IGNORED;
    b[0].image = s->img;
    b[0].subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    b[0].subresourceRange.levelCount = 1;
    b[0].subresourceRange.layerCount = 1;
    // The core's image (GENERAL, written by its copy).
    b[1] = b[0];
    b[1].srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT | VK_ACCESS_SHADER_WRITE_BIT;
    b[1].dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
    b[1].oldLayout = VK_IMAGE_LAYOUT_GENERAL;
    b[1].newLayout = VK_IMAGE_LAYOUT_GENERAL;
    b[1].srcQueueFamilyIndex = b[1].dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b[1].image = v->handed;
    v->vkCmdPipelineBarrier(s->cb, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, NULL, 0, NULL, 2, b);
    VkImageBlit blit;
    memset(&blit, 0, sizeof blit);
    blit.srcSubresource.aspectMask = blit.dstSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    blit.srcSubresource.layerCount = blit.dstSubresource.layerCount = 1;
    blit.srcOffsets[1].x = blit.dstOffsets[1].x = w;
    blit.srcOffsets[1].y = blit.dstOffsets[1].y = hh;
    blit.srcOffsets[1].z = blit.dstOffsets[1].z = 1;
    v->vkCmdBlitImage(s->cb, v->handed, VK_IMAGE_LAYOUT_GENERAL, s->img, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &blit, VK_FILTER_NEAREST);
    // To GL: GENERAL, released to the external queue family.
    VkImageMemoryBarrier out = b[0];
    out.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    out.dstAccessMask = 0;
    out.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    out.newLayout = VK_IMAGE_LAYOUT_GENERAL;
    out.srcQueueFamilyIndex = v->interop ? v->family : VK_QUEUE_FAMILY_IGNORED;
    out.dstQueueFamilyIndex = v->interop ? VK_QUEUE_FAMILY_EXTERNAL : VK_QUEUE_FAMILY_IGNORED;
    v->vkCmdPipelineBarrier(s->cb, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, 0, 0, NULL, 0, NULL, 1, &out);
    v->vkEndCommandBuffer(s->cb);
    VkPipelineStageFlags wait_stage = VK_PIPELINE_STAGE_TRANSFER_BIT;
    VkSubmitInfo si = {VK_STRUCTURE_TYPE_SUBMIT_INFO};
    si.commandBufferCount = 1;
    si.pCommandBuffers = &s->cb;
    if (v->interop && s->done_pending) {
        si.waitSemaphoreCount = 1;
        si.pWaitSemaphores = &s->done;
        si.pWaitDstStageMask = &wait_stage;
    }
    if (v->interop) {
        si.signalSemaphoreCount = 1;
        si.pSignalSemaphores = &s->ready;
    }
    submit(v, &si, s->fence);
    s->fence_pending = 1;
    s->done_pending = 0;
    s->external = v->interop;
    // The core's sync index is done once everything before it is: its image can be reused then.
    wait_index(v, v->sync_index);
    submit(v, NULL, v->sync_fence[v->sync_index]);
    v->sync_pending[v->sync_index] = 1;
    v->sync_index ^= 1u;
    pthread_mutex_lock(&v->slock);
    for (int i = 0; i < WLVK_SLOTS; ++i)
        if (v->slots[i].state == SLOT_READY) v->slots[i].state = SLOT_STALE;  // never shown: GL still takes its semaphore
    s->state = SLOT_READY;
    s->seq = ++v->seq;
    v->last_slot = pick;
    pthread_mutex_unlock(&v->slock);
    return 1;
}

// The newest frame to the CPU as XRGB8888 rows (B, G, R, x): for a window GL cannot share with (another GPU, no
// import extension) and for the recorder. Waits for the frame. Returns 1 with *w/*h set.
int wlvk_readback(void *h, void *xrgb, int max_bytes, int *w, int *hh) {
    wlvk *v = h;
    if (!v || !v->handed) return 0;
    // The core's image as it stands this frame (before wlvk_frame or instead of it).
    int fw = *w, fh = *hh;
    VkDeviceSize bytes = (VkDeviceSize)fw * fh * 4;
    if (fw <= 0 || fh <= 0 || bytes > (VkDeviceSize)max_bytes) return 0;
    if (v->rb_size < bytes) {
        if (v->rb_map) v->vkUnmapMemory(v->dev, v->rb_mem);
        if (v->rb_buf) v->vkDestroyBuffer(v->dev, v->rb_buf, NULL);
        if (v->rb_mem) v->vkFreeMemory(v->dev, v->rb_mem, NULL);
        v->rb_map = NULL;
        VkBufferCreateInfo bci = {VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO, NULL, 0, bytes, VK_BUFFER_USAGE_TRANSFER_DST_BIT, VK_SHARING_MODE_EXCLUSIVE};
        if (v->vkCreateBuffer(v->dev, &bci, NULL, &v->rb_buf) != VK_SUCCESS) return 0;
        VkMemoryRequirements req;
        v->vkGetBufferMemoryRequirements(v->dev, v->rb_buf, &req);
        uint32_t type = memory_type(v, req.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT | VK_MEMORY_PROPERTY_HOST_CACHED_BIT);
        if (type == UINT32_MAX) type = memory_type(v, req.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
        VkMemoryAllocateInfo mai = {VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO, NULL, req.size, type};
        if (type == UINT32_MAX || v->vkAllocateMemory(v->dev, &mai, NULL, &v->rb_mem) != VK_SUCCESS) return 0;
        v->vkBindBufferMemory(v->dev, v->rb_buf, v->rb_mem, 0);
        v->vkMapMemory(v->dev, v->rb_mem, 0, VK_WHOLE_SIZE, 0, &v->rb_map);
        v->rb_size = bytes;
    }
    VkCommandBufferBeginInfo bi = {VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO, NULL, VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT};
    v->vkBeginCommandBuffer(v->rb_cb, &bi);
    VkMemoryBarrier mb = {VK_STRUCTURE_TYPE_MEMORY_BARRIER, NULL, VK_ACCESS_MEMORY_WRITE_BIT, VK_ACCESS_TRANSFER_READ_BIT};
    v->vkCmdPipelineBarrier(v->rb_cb, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 1, &mb, 0, NULL, 0, NULL);
    VkBufferImageCopy r;
    memset(&r, 0, sizeof r);
    r.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    r.imageSubresource.layerCount = 1;
    r.imageExtent.width = (uint32_t)fw;
    r.imageExtent.height = (uint32_t)fh;
    r.imageExtent.depth = 1;
    v->vkCmdCopyImageToBuffer(v->rb_cb, v->handed, VK_IMAGE_LAYOUT_GENERAL, v->rb_buf, 1, &r);
    VkMemoryBarrier hb = {VK_STRUCTURE_TYPE_MEMORY_BARRIER, NULL, VK_ACCESS_TRANSFER_WRITE_BIT, VK_ACCESS_HOST_READ_BIT};
    v->vkCmdPipelineBarrier(v->rb_cb, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_HOST_BIT, 0, 1, &hb, 0, NULL, 0, NULL);
    v->vkEndCommandBuffer(v->rb_cb);
    VkSubmitInfo si = {VK_STRUCTURE_TYPE_SUBMIT_INFO};
    si.commandBufferCount = 1;
    si.pCommandBuffers = &v->rb_cb;
    submit(v, &si, v->rb_fence);
    v->vkWaitForFences(v->dev, 1, &v->rb_fence, VK_TRUE, UINT64_MAX);
    v->vkResetFences(v->dev, 1, &v->rb_fence);
    memcpy(xrgb, v->rb_map, (size_t)bytes);
    return 1;
}

// After the core's context_destroy: everything of ours goes, then the device (when we made it) and the instance.
void wlvk_destroy(void *h) {
    wlvk *v = h;
    if (!v) return;
    pthread_mutex_lock(&v->qlock);
    v->vkQueueWaitIdle(v->queue);
    pthread_mutex_unlock(&v->qlock);
    for (int i = 0; i < WLVK_SLOTS; ++i) {
        wlvk_slot *s = &v->slots[i];
        destroy_slot_image(v, s);
        if (s->ready) v->vkDestroySemaphore(v->dev, s->ready, NULL);
        if (s->done) v->vkDestroySemaphore(v->dev, s->done, NULL);
        if (s->fence) v->vkDestroyFence(v->dev, s->fence, NULL);
    }
    for (int i = 0; i < 2; ++i) v->vkDestroyFence(v->dev, v->sync_fence[i], NULL);
    if (v->rb_map) v->vkUnmapMemory(v->dev, v->rb_mem);
    if (v->rb_buf) v->vkDestroyBuffer(v->dev, v->rb_buf, NULL);
    if (v->rb_mem) v->vkFreeMemory(v->dev, v->rb_mem, NULL);
    v->vkDestroyFence(v->dev, v->rb_fence, NULL);
    v->vkDestroyCommandPool(v->dev, v->pool, NULL);
    if (v->neg && v->neg->destroy_device) v->neg->destroy_device();
    v->vkDestroyDevice(v->dev, NULL);  // the frontend owns the device, whoever made it
    v->vkDestroyInstance(v->inst, NULL);
    dlclose(v->lib);
    if (g_vk == v) g_vk = NULL;
    free(v);
}

// ── the present thread: GL imports ────────────────────────────────────────────────────────────────────────────────
static struct {
    PFNGLCREATEMEMORYOBJECTSEXTPROC CreateMemoryObjectsEXT;
    PFNGLDELETEMEMORYOBJECTSEXTPROC DeleteMemoryObjectsEXT;
    PFNGLMEMORYOBJECTPARAMETERIVEXTPROC MemoryObjectParameterivEXT;
    PFNGLIMPORTMEMORYFDEXTPROC ImportMemoryFdEXT;
    PFNGLTEXSTORAGEMEM2DEXTPROC TexStorageMem2DEXT;
    PFNGLGENSEMAPHORESEXTPROC GenSemaphoresEXT;
    PFNGLDELETESEMAPHORESEXTPROC DeleteSemaphoresEXT;
    PFNGLIMPORTSEMAPHOREFDEXTPROC ImportSemaphoreFdEXT;
    PFNGLWAITSEMAPHOREEXTPROC WaitSemaphoreEXT;
    PFNGLSIGNALSEMAPHOREEXTPROC SignalSemaphoreEXT;
    PFNGLGETUNSIGNEDBYTEVEXTPROC GetUnsignedBytevEXT;
    PFNGLGETUNSIGNEDBYTEI_VEXTPROC GetUnsignedBytei_vEXT;
} gx;

static int gl_has(const char *name) {
    const char *all = (const char *)glGetString(GL_EXTENSIONS);
    if (!all) return 0;
    size_t n = strlen(name);
    for (const char *p = all; (p = strstr(p, name)); p += n)
        if ((p == all || p[-1] == ' ') && (p[n] == ' ' || p[n] == 0)) return 1;
    return 0;
}

// Once, on the present thread: the import extensions, and the same GPU and driver on both sides.
static int gl_check(wlvk *v) {
    if (v->gl_checked) return v->gl_ok;
    v->gl_checked = 1;
    v->gl_ok = 0;
    if (!v->interop) return 0;
    if (!gl_has("GL_EXT_memory_object") || !gl_has("GL_EXT_memory_object_fd") || !gl_has("GL_EXT_semaphore") || !gl_has("GL_EXT_semaphore_fd")) {
        vk_log("GL has no memory/semaphore import: frames go through the CPU");
        return 0;
    }
#define GX(f) gx.f = (void *)eglGetProcAddress("gl" #f); if (!gx.f) return 0;
    GX(CreateMemoryObjectsEXT) GX(DeleteMemoryObjectsEXT) GX(MemoryObjectParameterivEXT) GX(ImportMemoryFdEXT) GX(TexStorageMem2DEXT)
    GX(GenSemaphoresEXT) GX(DeleteSemaphoresEXT) GX(ImportSemaphoreFdEXT) GX(WaitSemaphoreEXT) GX(SignalSemaphoreEXT) GX(GetUnsignedBytevEXT) GX(GetUnsignedBytei_vEXT)
#undef GX
    GLubyte dev[GL_UUID_SIZE_EXT], drv[GL_UUID_SIZE_EXT];
    GLint n = 0;
    glGetIntegerv(GL_NUM_DEVICE_UUIDS_EXT, &n);
    int same = 0;
    for (GLint i = 0; i < n && !same; ++i) {
        gx.GetUnsignedBytei_vEXT(GL_DEVICE_UUID_EXT, (GLuint)i, dev);
        same = memcmp(dev, v->device_uuid, VK_UUID_SIZE) == 0;
    }
    gx.GetUnsignedBytevEXT(GL_DRIVER_UUID_EXT, drv);
    if (!same || memcmp(drv, v->driver_uuid, VK_UUID_SIZE) != 0) {
        vk_log("GL and Vulkan are on different devices or drivers: frames go through the CPU");
        return 0;
    }
    v->gl_ok = 1;
    return 1;
}

static void gl_import(wlvk *v, wlvk_slot *s, int smooth) {
    if (!s->gl_sems) {
        int fd = -1;
        VkSemaphoreGetFdInfoKHR gi = {VK_STRUCTURE_TYPE_SEMAPHORE_GET_FD_INFO_KHR, NULL, s->ready, VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_OPAQUE_FD_BIT};
        gx.GenSemaphoresEXT(1, &s->gl_ready);
        if (v->vkGetSemaphoreFdKHR(v->dev, &gi, &fd) == VK_SUCCESS) gx.ImportSemaphoreFdEXT(s->gl_ready, GL_HANDLE_TYPE_OPAQUE_FD_EXT, fd);
        gi.semaphore = s->done;
        gx.GenSemaphoresEXT(1, &s->gl_done);
        if (v->vkGetSemaphoreFdKHR(v->dev, &gi, &fd) == VK_SUCCESS) gx.ImportSemaphoreFdEXT(s->gl_done, GL_HANDLE_TYPE_OPAQUE_FD_EXT, fd);
        s->gl_sems = 1;
    }
    if (s->gl_gen == s->gen) return;
    if (s->gl_tex) glDeleteTextures(1, &s->gl_tex);
    if (s->gl_mem) gx.DeleteMemoryObjectsEXT(1, &s->gl_mem);
    int fd = -1;
    VkMemoryGetFdInfoKHR mi = {VK_STRUCTURE_TYPE_MEMORY_GET_FD_INFO_KHR, NULL, s->mem, VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_FD_BIT};
    if (v->vkGetMemoryFdKHR(v->dev, &mi, &fd) != VK_SUCCESS) return;
    gx.CreateMemoryObjectsEXT(1, &s->gl_mem);
    GLint dedicated = GL_TRUE;
    gx.MemoryObjectParameterivEXT(s->gl_mem, GL_DEDICATED_MEMORY_OBJECT_EXT, &dedicated);
    gx.ImportMemoryFdEXT(s->gl_mem, s->memsize, GL_HANDLE_TYPE_OPAQUE_FD_EXT, fd);  // GL owns the fd now
    glGenTextures(1, &s->gl_tex);
    glBindTexture(GL_TEXTURE_2D, s->gl_tex);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_TILING_EXT, GL_OPTIMAL_TILING_EXT);
    gx.TexStorageMem2DEXT(GL_TEXTURE_2D, 1, GL_RGBA8, s->w, s->h, s->gl_mem, 0);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_SWIZZLE_A, GL_ONE);  // XRGB: the fourth byte means nothing
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    GLint f = smooth ? GL_LINEAR : GL_NEAREST;
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, f);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, f);
    s->gl_gen = s->gen;
}

// Shows the newest frame the core handed over: 1 drawn and swapped, 0 no frame yet (nothing done), -1 GL cannot
// take frames on the GPU (the caller presents CPU frames from wlvk_readback instead).
int wlp_present_vk(void *wh, void *h) {
    wlvk *v = h;
    if (!v || !wh) return -1;
    if (!gl_check(v)) return -1;
    const int smooth = wlp_shader_smooth(wh);
    int fresh = -1, retired = -1, stale[WLVK_SLOTS], nstale = 0;
    pthread_mutex_lock(&v->slock);
    for (int i = 0; i < WLVK_SLOTS; ++i) {
        if (v->slots[i].state == SLOT_STALE) stale[nstale++] = i;
        if (v->slots[i].state == SLOT_READY && (fresh < 0 || v->slots[i].seq > v->slots[fresh].seq)) fresh = i;
    }
    if (fresh >= 0) {
        if (v->shown >= 0) retired = v->shown;
        v->slots[fresh].state = SLOT_SHOWN;
        v->shown = fresh;
    }
    const int show = v->shown;
    pthread_mutex_unlock(&v->slock);
    GLenum general = GL_LAYOUT_GENERAL_EXT;
    for (int k = 0; k < nstale; ++k) {
        wlvk_slot *s = &v->slots[stale[k]];
        gl_import(v, s, smooth);
        gx.WaitSemaphoreEXT(s->gl_ready, 0, NULL, 1, &s->gl_tex, &general);
        gx.SignalSemaphoreEXT(s->gl_done, 0, NULL, 1, &s->gl_tex, &general);
        pthread_mutex_lock(&v->slock);
        s->done_pending = 1;
        s->state = SLOT_FREE;
        pthread_mutex_unlock(&v->slock);
    }
    if (show < 0) return 0;
    wlvk_slot *s = &v->slots[show];
    gl_import(v, s, smooth);
    if (fresh >= 0) gx.WaitSemaphoreEXT(s->gl_ready, 0, NULL, 1, &s->gl_tex, &general);
    const int r = wlp_present_tex(wh, s->gl_tex, s->w, s->h);
    if (retired >= 0) {
        // The slot shown until now: GL is done with it after the commands issued so far.
        wlvk_slot *o = &v->slots[retired];
        gx.SignalSemaphoreEXT(o->gl_done, 0, NULL, 1, &o->gl_tex, &general);
        pthread_mutex_lock(&v->slock);
        o->done_pending = 1;
        o->state = SLOT_FREE;
        pthread_mutex_unlock(&v->slock);
    }
    return r < 0 ? -1 : 1;
}

// The GL objects of every slot, before the GL context goes (present thread).
void wlp_release_vk(void *h) {
    wlvk *v = h;
    if (!v || !v->gl_ok) return;
    for (int i = 0; i < WLVK_SLOTS; ++i) {
        wlvk_slot *s = &v->slots[i];
        if (s->gl_tex) glDeleteTextures(1, &s->gl_tex);
        if (s->gl_mem) gx.DeleteMemoryObjectsEXT(1, &s->gl_mem);
        if (s->gl_ready) gx.DeleteSemaphoresEXT(1, &s->gl_ready);
        if (s->gl_done) gx.DeleteSemaphoresEXT(1, &s->gl_done);
        s->gl_tex = s->gl_mem = s->gl_ready = s->gl_done = 0;
        s->gl_gen = -1;
        s->gl_sems = 0;
    }
}
