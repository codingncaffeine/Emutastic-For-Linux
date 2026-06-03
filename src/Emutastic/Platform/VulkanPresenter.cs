using System;
using System.Runtime.InteropServices;
using static Emutastic.Platform.Vk;

namespace Emutastic.Platform
{
    /// <summary>
    /// Presents a software libretro frame (BGRA8888) through a Vulkan FIFO swapchain on an X11 window.
    /// The blocking vsync present is the frame clock — call <see cref="Present"/> from the emu thread and
    /// it paces the loop to the compositor's panel-locked cadence (RetroArch's model). See
    /// docs/frame-pacing-and-vsync.md.
    ///
    /// Pacing-correct design (the smooth pattern, not the naive one): N=2 frames in flight (per-frame
    /// command buffer + image-available semaphore + in-flight fence + upload resources), a render-finished
    /// semaphore PER SWAPCHAIN IMAGE, ≥3 swapchain images, and the fence is waited at the TOP of a slot's
    /// next reuse — never blocking right after present. A single-frame design (wait fence immediately after
    /// present, too few images) stalls ~3 vblanks per frame under a compositor; this pipelines instead.
    /// </summary>
    public sealed unsafe class VulkanPresenter : IDisposable
    {
        const int FRAMES = 2;   // frames in flight

        private IntPtr _instance, _gpu, _device, _queue, _surface, _swapchain, _cmdPool;
        private uint _queueFamily;
        private VkFormat _scFormat;
        private uint _scW, _scH;
        private IntPtr[] _scImages = Array.Empty<IntPtr>();
        private IntPtr[] _renderDone = Array.Empty<IntPtr>(); // per swapchain image

        // Per-frame-slot objects (FRAMES of each).
        private readonly IntPtr[] _cmd = new IntPtr[FRAMES];
        private readonly IntPtr[] _imgAvail = new IntPtr[FRAMES];
        private readonly IntPtr[] _inFlight = new IntPtr[FRAMES];
        // Per-frame-slot upload (staging buffer → optimal device-local blit-source image), double-buffered
        // so a frame's upload can't race the previous frame's in-flight GPU read.
        private readonly IntPtr[] _staging = new IntPtr[FRAMES];
        private readonly IntPtr[] _stagingMem = new IntPtr[FRAMES];
        private readonly IntPtr[] _stagingMapped = new IntPtr[FRAMES];
        private readonly IntPtr[] _srcImage = new IntPtr[FRAMES];
        private readonly IntPtr[] _srcImageMem = new IntPtr[FRAMES];
        private readonly int[] _srcW = new int[FRAMES];
        private readonly int[] _srcH = new int[FRAMES];
        private readonly ulong[] _stagingSize = new ulong[FRAMES];
        private int _frame;     // current frame slot

        private int _clientW, _clientH;

        private PFN_DestroySurfaceKHR? _destroySurface;
        private PFN_GetPhysicalDeviceSurfaceCapabilitiesKHR? _getCaps;

        public string? LastError { get; private set; }
        private bool _presentWaitOk;          // device has VK_KHR_present_id + present_wait enabled
        private ulong _presentId;             // monotonic per-swapchain present id (for vkWaitForPresentKHR)
        public bool PresentWaitAvailable => _presentWaitOk;

        public static VulkanPresenter? TryCreate(IntPtr xDisplay, IntPtr xWindow, int width, int height, out string? error)
        {
            error = null;
            try { return new VulkanPresenter(xDisplay, xWindow, width, height); }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        private VulkanPresenter(IntPtr xDisplay, IntPtr xWindow, int width, int height)
        {
            _clientW = Math.Max(1, width); _clientH = Math.Max(1, height);
            CreateInstance();
            CreateSurface(xDisplay, xWindow);
            PickGpuAndQueue();
            CreateDevice();
            CreateFrameObjects();
            CreateSwapchain((uint)_clientW, (uint)_clientH);
        }

        // ── instance ──
        private void CreateInstance()
        {
            string[] exts = { "VK_KHR_surface", "VK_KHR_xlib_surface" };
            using var extArr = new NativeStrArray(exts);
            var app = new VkApplicationInfo { sType = VkStructureType.APPLICATION_INFO, apiVersion = MakeVersion(1, 1, 0) };
            IntPtr appPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VkApplicationInfo>());
            Marshal.StructureToPtr(app, appPtr, false);
            try
            {
                var ci = new VkInstanceCreateInfo
                {
                    sType = VkStructureType.INSTANCE_CREATE_INFO,
                    pApplicationInfo = appPtr,
                    enabledExtensionCount = (uint)exts.Length, ppEnabledExtensionNames = extArr.Ptr,
                };
                Check(vkCreateInstance(ref ci, IntPtr.Zero, out _instance), "vkCreateInstance");
            }
            finally { Marshal.FreeHGlobal(appPtr); }
        }

        private void CreateSurface(IntPtr xDisplay, IntPtr xWindow)
        {
            var create = Load<PFN_CreateXlibSurfaceKHR>(_instance, "vkCreateXlibSurfaceKHR");
            _destroySurface = Load<PFN_DestroySurfaceKHR>(_instance, "vkDestroySurfaceKHR");
            _getCaps = Load<PFN_GetPhysicalDeviceSurfaceCapabilitiesKHR>(_instance, "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
            var ci = new VkXlibSurfaceCreateInfoKHR { sType = VkStructureType.XLIB_SURFACE_CREATE_INFO_KHR, dpy = xDisplay, window = xWindow };
            Check(create(_instance, ref ci, IntPtr.Zero, out _surface), "vkCreateXlibSurfaceKHR");
        }

        private void PickGpuAndQueue()
        {
            var support = Load<PFN_GetPhysicalDeviceSurfaceSupportKHR>(_instance, "vkGetPhysicalDeviceSurfaceSupportKHR");
            uint count = 0;
            Check(vkEnumeratePhysicalDevices(_instance, ref count, IntPtr.Zero), "vkEnumeratePhysicalDevices");
            if (count == 0) throw new InvalidOperationException("no Vulkan physical devices");
            IntPtr devs = Marshal.AllocHGlobal((int)count * IntPtr.Size);
            try
            {
                Check(vkEnumeratePhysicalDevices(_instance, ref count, devs), "vkEnumeratePhysicalDevices");
                for (int d = 0; d < count; d++)
                {
                    IntPtr gpu = Marshal.ReadIntPtr(devs, d * IntPtr.Size);
                    uint qfCount = 0;
                    vkGetPhysicalDeviceQueueFamilyProperties(gpu, ref qfCount, IntPtr.Zero);
                    if (qfCount == 0) continue;
                    int stride = Marshal.SizeOf<VkQueueFamilyProperties>();
                    IntPtr qfBuf = Marshal.AllocHGlobal((int)qfCount * stride);
                    try
                    {
                        vkGetPhysicalDeviceQueueFamilyProperties(gpu, ref qfCount, qfBuf);
                        for (uint q = 0; q < qfCount; q++)
                        {
                            var qf = Marshal.PtrToStructure<VkQueueFamilyProperties>(qfBuf + (int)q * stride);
                            support(gpu, q, _surface, out uint pres);
                            if ((qf.queueFlags & VkQueueFlags.GRAPHICS) != 0 && pres != 0) { _gpu = gpu; _queueFamily = q; return; }
                        }
                    }
                    finally { Marshal.FreeHGlobal(qfBuf); }
                }
            }
            finally { Marshal.FreeHGlobal(devs); }
            throw new InvalidOperationException("no graphics+present queue family");
        }

        private void CreateDevice()
        {
            // Prefer a device with VK_KHR_present_id + VK_KHR_present_wait (for lockstep-to-real-present
            // pacing). If that fails (driver lacks them), fall back to a plain swapchain device.
            if (TryCreateDevice(true)) { _presentWaitOk = true; }
            else if (TryCreateDevice(false)) { _presentWaitOk = false; System.Diagnostics.Trace.WriteLine("[Vk] present_wait unavailable — falling back to acquire-paced present"); }
            else throw new InvalidOperationException("vkCreateDevice failed");
            vkGetDeviceQueue(_device, _queueFamily, 0, out _queue);
        }

        private bool TryCreateDevice(bool withPresentWait)
        {
            IntPtr prio = Marshal.AllocHGlobal(sizeof(float));
            Marshal.Copy(new[] { 1.0f }, 0, prio, 1);
            var qci = new VkDeviceQueueCreateInfo { sType = VkStructureType.DEVICE_QUEUE_CREATE_INFO, queueFamilyIndex = _queueFamily, queueCount = 1, pQueuePriorities = prio };
            IntPtr qciPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VkDeviceQueueCreateInfo>());
            Marshal.StructureToPtr(qci, qciPtr, false);
            var exts = withPresentWait
                ? new[] { "VK_KHR_swapchain", "VK_KHR_present_id", "VK_KHR_present_wait" }
                : new[] { "VK_KHR_swapchain" };
            using var devExts = new NativeStrArray(exts);
            IntPtr idFeatPtr = IntPtr.Zero, waitFeatPtr = IntPtr.Zero;
            try
            {
                IntPtr pNext = IntPtr.Zero;
                if (withPresentWait)
                {
                    // Chain: ci.pNext → PresentIdFeatures → PresentWaitFeatures → null (features MUST be enabled).
                    waitFeatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VkPhysicalDevicePresentWaitFeaturesKHR>());
                    idFeatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VkPhysicalDevicePresentIdFeaturesKHR>());
                    Marshal.StructureToPtr(new VkPhysicalDevicePresentWaitFeaturesKHR { sType = VkStructureType.PHYS_DEV_PRESENT_WAIT_FEATURES_KHR, pNext = IntPtr.Zero, presentWait = 1 }, waitFeatPtr, false);
                    Marshal.StructureToPtr(new VkPhysicalDevicePresentIdFeaturesKHR { sType = VkStructureType.PHYS_DEV_PRESENT_ID_FEATURES_KHR, pNext = waitFeatPtr, presentId = 1 }, idFeatPtr, false);
                    pNext = idFeatPtr;
                }
                var ci = new VkDeviceCreateInfo
                {
                    sType = VkStructureType.DEVICE_CREATE_INFO, pNext = pNext,
                    queueCreateInfoCount = 1, pQueueCreateInfos = qciPtr,
                    enabledExtensionCount = (uint)exts.Length, ppEnabledExtensionNames = devExts.Ptr,
                };
                return vkCreateDevice(_gpu, ref ci, IntPtr.Zero, out _device) == VkResult.VK_SUCCESS;
            }
            finally
            {
                Marshal.FreeHGlobal(qciPtr); Marshal.FreeHGlobal(prio);
                if (idFeatPtr != IntPtr.Zero) Marshal.FreeHGlobal(idFeatPtr);
                if (waitFeatPtr != IntPtr.Zero) Marshal.FreeHGlobal(waitFeatPtr);
            }
        }

        private void CreateFrameObjects()
        {
            var pci = new VkCommandPoolCreateInfo { sType = VkStructureType.COMMAND_POOL_CREATE_INFO, flags = VkCommandPoolCreate.RESET_COMMAND_BUFFER, queueFamilyIndex = _queueFamily };
            Check(vkCreateCommandPool(_device, ref pci, IntPtr.Zero, out _cmdPool), "vkCreateCommandPool");
            for (int i = 0; i < FRAMES; i++)
            {
                var ai = new VkCommandBufferAllocateInfo { sType = VkStructureType.COMMAND_BUFFER_ALLOCATE_INFO, commandPool = _cmdPool, level = 0, commandBufferCount = 1 };
                Check(vkAllocateCommandBuffers(_device, ref ai, out _cmd[i]), "vkAllocateCommandBuffers");
                var sci = new VkSemaphoreCreateInfo { sType = VkStructureType.SEMAPHORE_CREATE_INFO };
                Check(vkCreateSemaphore(_device, ref sci, IntPtr.Zero, out _imgAvail[i]), "vkCreateSemaphore(imgAvail)");
                var fci = new VkFenceCreateInfo { sType = VkStructureType.FENCE_CREATE_INFO, flags = 1 /*SIGNALED*/ };
                Check(vkCreateFence(_device, ref fci, IntPtr.Zero, out _inFlight[i]), "vkCreateFence");
            }
        }

        // ── swapchain (FIFO = vsync), ≥3 images, render-finished semaphore per image ──
        private void CreateSwapchain(uint width, uint height)
        {
            var getFormats = Load<PFN_GetPhysicalDeviceSurfaceFormatsKHR>(_instance, "vkGetPhysicalDeviceSurfaceFormatsKHR");
            _getCaps!(_gpu, _surface, out VkSurfaceCapabilitiesKHR caps);

            uint w = caps.currentExtent.width, h = caps.currentExtent.height;
            if (w == 0xFFFFFFFF || h == 0xFFFFFFFF) { w = width; h = height; }
            w = Math.Clamp(w, caps.minImageExtent.width, caps.maxImageExtent.width);
            h = Math.Clamp(h, caps.minImageExtent.height, caps.maxImageExtent.height);
            if (w == 0 || h == 0) { w = Math.Max(1, width); h = Math.Max(1, height); }

            uint fmtCount = 0; getFormats(_gpu, _surface, ref fmtCount, IntPtr.Zero);
            VkFormat chosen = VkFormat.B8G8R8A8_UNORM; VkColorSpaceKHR space = VkColorSpaceKHR.SRGB_NONLINEAR_KHR;
            if (fmtCount > 0)
            {
                int stride = Marshal.SizeOf<VkSurfaceFormatKHR>();
                IntPtr buf = Marshal.AllocHGlobal((int)fmtCount * stride);
                try
                {
                    getFormats(_gpu, _surface, ref fmtCount, buf);
                    VkSurfaceFormatKHR first = default; bool found = false;
                    for (uint i = 0; i < fmtCount; i++)
                    {
                        var f = Marshal.PtrToStructure<VkSurfaceFormatKHR>(buf + (int)i * stride);
                        if (i == 0) first = f;
                        if (f.format == VkFormat.B8G8R8A8_UNORM) { chosen = f.format; space = f.colorSpace; found = true; break; }
                    }
                    if (!found) { chosen = first.format == VkFormat.UNDEFINED ? VkFormat.B8G8R8A8_UNORM : first.format; space = first.colorSpace; }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }

            // Swapchain image count (≥3 so acquire rarely blocks on a compositor buffer release; more =
            // smoother delivery at a little more latency). Tunable for A/B via EMUTASTIC_SWAPCHAIN_IMAGES.
            // Default 3 (the spike's clean-60 count / RetroArch's default): tight enough that FIFO present
            // paces to the vblank, but not so tight it stalls. 4 added slack → uneven present (13–17ms
            // bounce → coupled-emulation jitter); 2 risks under-buffer stalls. Tunable via
            // EMUTASTIC_SWAPCHAIN_IMAGES for A/B.
            uint desired = (uint)Math.Clamp(EnvInt("EMUTASTIC_SWAPCHAIN_IMAGES", 3), 2, 8);
            uint minImages = Math.Max(desired, caps.minImageCount);
            if (caps.maxImageCount > 0 && minImages > caps.maxImageCount) minImages = caps.maxImageCount;

            // Present mode: FIFO_RELAXED (adaptive vsync) by default — a late frame tears slightly instead
            // of stalling a whole extra refresh (the 60fps-but-juddery case). EMUTASTIC_PRESENT_MODE=fifo
            // forces strict FIFO. (MAILBOX isn't offered on Xwayland.)
            VkPresentModeKHR mode = PickPresentMode();
            System.Diagnostics.Trace.WriteLine($"[Vk] swapchain {w}x{h} images={minImages} present={mode}");

            var ci = new VkSwapchainCreateInfoKHR
            {
                sType = VkStructureType.SWAPCHAIN_CREATE_INFO_KHR, surface = _surface,
                minImageCount = minImages, imageFormat = chosen, imageColorSpace = space,
                imageExtent = new VkExtent2D { width = w, height = h }, imageArrayLayers = 1,
                imageUsage = VkImageUsage.TRANSFER_DST | VkImageUsage.COLOR_ATTACHMENT,
                imageSharingMode = VkSharingMode.EXCLUSIVE,
                preTransform = caps.currentTransform, compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR,
                presentMode = mode, clipped = 1, oldSwapchain = IntPtr.Zero,
            };
            Check(vkCreateSwapchainKHR(_device, ref ci, IntPtr.Zero, out _swapchain), "vkCreateSwapchainKHR");
            _scFormat = chosen; _scW = w; _scH = h; _presentId = 0;   // present ids restart per swapchain

            uint imgCount = 0;
            Check(vkGetSwapchainImagesKHR(_device, _swapchain, ref imgCount, IntPtr.Zero), "vkGetSwapchainImagesKHR");
            IntPtr imgBuf = Marshal.AllocHGlobal((int)imgCount * IntPtr.Size);
            try
            {
                Check(vkGetSwapchainImagesKHR(_device, _swapchain, ref imgCount, imgBuf), "vkGetSwapchainImagesKHR");
                _scImages = new IntPtr[imgCount];
                for (int i = 0; i < imgCount; i++) _scImages[i] = Marshal.ReadIntPtr(imgBuf, i * IntPtr.Size);
            }
            finally { Marshal.FreeHGlobal(imgBuf); }

            // render-finished semaphore per swapchain image (avoids the present/submit semaphore-reuse hazard).
            _renderDone = new IntPtr[imgCount];
            for (int i = 0; i < imgCount; i++)
            {
                var sci = new VkSemaphoreCreateInfo { sType = VkStructureType.SEMAPHORE_CREATE_INFO };
                Check(vkCreateSemaphore(_device, ref sci, IntPtr.Zero, out _renderDone[i]), "vkCreateSemaphore(renderDone)");
            }
        }

        // Pick FIFO_RELAXED (adaptive vsync) when available, else strict FIFO. Env override: fifo|relaxed.
        private VkPresentModeKHR PickPresentMode()
        {
            // Strict FIFO by default: every present blocks to the next vblank → EVEN refresh intervals,
            // which is what the coupled phase-lock needs. FIFO_RELAXED (opt-in) lets late frames present
            // early → uneven intervals → the very jitter we're killing.
            string want = Environment.GetEnvironmentVariable("EMUTASTIC_PRESENT_MODE") ?? "fifo";
            if (want.Equals("fifo", StringComparison.OrdinalIgnoreCase)) return VkPresentModeKHR.FIFO;
            var getModes = Load<PFN_GetPhysicalDeviceSurfacePresentModesKHR>(_instance, "vkGetPhysicalDeviceSurfacePresentModesKHR");
            uint n = 0; getModes(_gpu, _surface, ref n, IntPtr.Zero);
            if (n == 0) return VkPresentModeKHR.FIFO;
            IntPtr buf = Marshal.AllocHGlobal((int)n * sizeof(int));
            try
            {
                getModes(_gpu, _surface, ref n, buf);
                for (uint i = 0; i < n; i++)
                    if (Marshal.ReadInt32(buf, (int)i * sizeof(int)) == (int)VkPresentModeKHR.FIFO_RELAXED)
                        return VkPresentModeKHR.FIFO_RELAXED;
            }
            finally { Marshal.FreeHGlobal(buf); }
            return VkPresentModeKHR.FIFO;
        }

        private static int EnvInt(string key, int dflt)
            => int.TryParse(Environment.GetEnvironmentVariable(key), out int v) && v > 0 ? v : dflt;

        private void DestroySwapchain()
        {
            for (int i = 0; i < _renderDone.Length; i++)
                if (_renderDone[i] != IntPtr.Zero) vkDestroySemaphore(_device, _renderDone[i], IntPtr.Zero);
            _renderDone = Array.Empty<IntPtr>();
            if (_swapchain != IntPtr.Zero) { vkDestroySwapchainKHR(_device, _swapchain, IntPtr.Zero); _swapchain = IntPtr.Zero; }
            _scImages = Array.Empty<IntPtr>();
        }

        public void Resize(int width, int height)
        {
            if (_device == IntPtr.Zero) return;
            _clientW = Math.Max(1, width); _clientH = Math.Max(1, height);
            RecreateSwapchain();
        }

        private void RecreateSwapchain()
        {
            vkDeviceWaitIdle(_device);
            DestroySwapchain();
            CreateSwapchain((uint)_clientW, (uint)_clientH);
        }

        // ── per-frame-slot source upload (staging buffer → optimal device-local blit-source image) ──
        private void EnsureSrc(int slot, int w, int h)
        {
            if (_srcImage[slot] != IntPtr.Zero && _srcW[slot] == w && _srcH[slot] == h) return;
            DestroySrc(slot);

            var ici = new VkImageCreateInfo
            {
                sType = VkStructureType.IMAGE_CREATE_INFO, imageType = VkImageType.T2D, format = VkFormat.B8G8R8A8_UNORM,
                extent = new VkExtent3D { width = (uint)w, height = (uint)h, depth = 1 },
                mipLevels = 1, arrayLayers = 1, samples = VK_SAMPLE_COUNT_1_BIT, tiling = VkImageTiling.OPTIMAL,
                usage = VkImageUsage.TRANSFER_DST | VkImageUsage.TRANSFER_SRC, sharingMode = VkSharingMode.EXCLUSIVE,
                initialLayout = VkImageLayout.UNDEFINED,
            };
            Check(vkCreateImage(_device, ref ici, IntPtr.Zero, out _srcImage[slot]), "vkCreateImage(src)");
            vkGetImageMemoryRequirements(_device, _srcImage[slot], out VkMemoryRequirements ireq);
            AllocBind(ireq, VkMemoryProperty.DEVICE_LOCAL, out _srcImageMem[slot], img: _srcImage[slot]);

            _stagingSize[slot] = (ulong)w * (uint)h * 4;
            var bci = new VkBufferCreateInfo { sType = VkStructureType.BUFFER_CREATE_INFO, size = _stagingSize[slot], usage = VkBufferUsage.TRANSFER_SRC, sharingMode = VkSharingMode.EXCLUSIVE };
            Check(vkCreateBuffer(_device, ref bci, IntPtr.Zero, out _staging[slot]), "vkCreateBuffer(staging)");
            vkGetBufferMemoryRequirements(_device, _staging[slot], out VkMemoryRequirements breq);
            AllocBind(breq, VkMemoryProperty.HOST_VISIBLE | VkMemoryProperty.HOST_COHERENT, out _stagingMem[slot], buf: _staging[slot]);
            Check(vkMapMemory(_device, _stagingMem[slot], 0, breq.size, 0, out _stagingMapped[slot]), "vkMapMemory(staging)");
            _srcW[slot] = w; _srcH[slot] = h;
        }

        private void AllocBind(VkMemoryRequirements req, VkMemoryProperty want, out IntPtr mem, IntPtr img = default, IntPtr buf = default)
        {
            uint type = FindMemoryType(req.memoryTypeBits, want);
            var ai = new VkMemoryAllocateInfo { sType = VkStructureType.MEMORY_ALLOCATE_INFO, allocationSize = req.size, memoryTypeIndex = type };
            Check(vkAllocateMemory(_device, ref ai, IntPtr.Zero, out mem), "vkAllocateMemory");
            if (img != IntPtr.Zero) Check(vkBindImageMemory(_device, img, mem, 0), "vkBindImageMemory");
            else Check(vkBindBufferMemory(_device, buf, mem, 0), "vkBindBufferMemory");
        }

        private void DestroySrc(int slot)
        {
            if (_stagingMapped[slot] != IntPtr.Zero) { vkUnmapMemory(_device, _stagingMem[slot]); _stagingMapped[slot] = IntPtr.Zero; }
            if (_staging[slot] != IntPtr.Zero) { vkDestroyBuffer(_device, _staging[slot], IntPtr.Zero); _staging[slot] = IntPtr.Zero; }
            if (_stagingMem[slot] != IntPtr.Zero) { vkFreeMemory(_device, _stagingMem[slot], IntPtr.Zero); _stagingMem[slot] = IntPtr.Zero; }
            if (_srcImage[slot] != IntPtr.Zero) { vkDestroyImage(_device, _srcImage[slot], IntPtr.Zero); _srcImage[slot] = IntPtr.Zero; }
            if (_srcImageMem[slot] != IntPtr.Zero) { vkFreeMemory(_device, _srcImageMem[slot], IntPtr.Zero); _srcImageMem[slot] = IntPtr.Zero; }
            _srcW[slot] = _srcH[slot] = 0;
        }

        private uint FindMemoryType(uint typeBits, VkMemoryProperty want)
        {
            vkGetPhysicalDeviceMemoryProperties(_gpu, out VkPhysicalDeviceMemoryProperties props);
            for (int i = 0; i < props.memoryTypeCount; i++)
                if ((typeBits & (1u << i)) != 0 && (props.GetMemoryType(i).propertyFlags & want) == want) return (uint)i;
            throw new InvalidOperationException("no suitable Vulkan memory type");
        }

        /// <summary>Upload one BGRA frame and present it. Pipelined (2 frames in flight) so the call blocks
        /// only on the FIFO vsync cadence, not on a full CPU↔GPU round-trip. Returns false if the frame was
        /// dropped (swapchain recreated).</summary>
        public bool Present(byte[] bgra, int frameW, int frameH)
        {
            if (_device == IntPtr.Zero || frameW <= 0 || frameH <= 0) return false;
            int frame = _frame;

            // Wait for THIS slot's previous submission (from 2 presents ago) — never the one we just made.
            vkWaitForFences(_device, 1, ref _inFlight[frame], 1, UINT64_MAX);

            EnsureSrc(frame, frameW, frameH);
            long bytes = (long)frameW * frameH * 4;
            fixed (byte* s0 = bgra)
                Buffer.MemoryCopy(s0, (byte*)_stagingMapped[frame], (long)_stagingSize[frame], bytes);

            var acq = vkAcquireNextImageKHR(_device, _swapchain, UINT64_MAX, _imgAvail[frame], IntPtr.Zero, out uint idx);
            if (acq == VkResult.VK_ERROR_OUT_OF_DATE_KHR) { RecreateSwapchain(); return false; }
            if (acq != VkResult.VK_SUCCESS && acq != VkResult.VK_SUBOPTIMAL_KHR) { LastError = $"vkAcquireNextImageKHR: {acq}"; return false; }

            vkResetFences(_device, 1, ref _inFlight[frame]);
            RecordAndSubmit(frame, idx, frameW, frameH);
            PresentImage(idx);

            _frame = (frame + 1) % FRAMES;
            return true;
        }

        private void PresentImage(uint idx)
        {
            IntPtr idxPtr = Marshal.AllocHGlobal(sizeof(uint));
            IntPtr scPtr = Marshal.AllocHGlobal(IntPtr.Size);
            IntPtr semPtr = Marshal.AllocHGlobal(IntPtr.Size);
            IntPtr pidPtr = IntPtr.Zero, idValPtr = IntPtr.Zero;
            try
            {
                Marshal.WriteInt32(idxPtr, (int)idx);
                Marshal.WriteIntPtr(scPtr, _swapchain);
                Marshal.WriteIntPtr(semPtr, _renderDone[idx]);
                var pi = new VkPresentInfoKHR
                {
                    sType = VkStructureType.PRESENT_INFO_KHR,
                    waitSemaphoreCount = 1, pWaitSemaphores = semPtr,
                    swapchainCount = 1, pSwapchains = scPtr, pImageIndices = idxPtr,
                };
                if (_presentWaitOk)
                {
                    // Tag this present with a monotonic id so vkWaitForPresentKHR can block until it's
                    // ACTUALLY on screen — the lockstep pacing signal.
                    _presentId++;
                    idValPtr = Marshal.AllocHGlobal(sizeof(ulong)); Marshal.WriteInt64(idValPtr, (long)_presentId);
                    pidPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VkPresentIdKHR>());
                    Marshal.StructureToPtr(new VkPresentIdKHR { sType = VkStructureType.PRESENT_ID_KHR, pNext = IntPtr.Zero, swapchainCount = 1, pPresentIds = idValPtr }, pidPtr, false);
                    pi.pNext = pidPtr;
                }
                var pr = vkQueuePresentKHR(_queue, ref pi);
                if (pr == VkResult.VK_ERROR_OUT_OF_DATE_KHR || pr == VkResult.VK_SUBOPTIMAL_KHR) RecreateSwapchain();
            }
            finally
            {
                Marshal.FreeHGlobal(idxPtr); Marshal.FreeHGlobal(scPtr); Marshal.FreeHGlobal(semPtr);
                if (pidPtr != IntPtr.Zero) Marshal.FreeHGlobal(pidPtr);
                if (idValPtr != IntPtr.Zero) Marshal.FreeHGlobal(idValPtr);
            }
        }

        /// <summary>Block until the most recent present has ACTUALLY been displayed (the lockstep pacing
        /// signal — CVDisplayLink equivalent). No-op if present_wait isn't available. Call once per frame
        /// right after Present, before producing the next frame.</summary>
        public void WaitForLastPresent(ulong timeoutNs = 100_000_000)
        {
            if (!_presentWaitOk || _presentId == 0 || _device == IntPtr.Zero || _swapchain == IntPtr.Zero) return;
            try { vkWaitForPresentKHR(_device, _swapchain, _presentId, timeoutNs); }
            catch { /* extension hiccup → just don't wait this frame */ }
        }

        private void RecordAndSubmit(int frame, uint idx, int frameW, int frameH)
        {
            IntPtr cmd = _cmd[frame], srcImage = _srcImage[frame], swapImage = _scImages[idx];
            vkResetCommandBuffer(cmd, 0);
            var bi = new VkCommandBufferBeginInfo { sType = VkStructureType.COMMAND_BUFFER_BEGIN_INFO, flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT };
            vkBeginCommandBuffer(cmd, ref bi);

            var range = new VkImageSubresourceRange { aspectMask = VK_IMAGE_ASPECT_COLOR_BIT, levelCount = 1, layerCount = 1 };

            Barrier(cmd, srcImage, VkImageLayout.UNDEFINED, VkImageLayout.TRANSFER_DST_OPTIMAL, VkAccess.NONE, VkAccess.TRANSFER_WRITE, VkPipelineStage.TOP_OF_PIPE, VkPipelineStage.TRANSFER, range);
            var copy = new VkBufferImageCopy { imageSubresource = new VkImageSubresourceLayers { aspectMask = VK_IMAGE_ASPECT_COLOR_BIT, layerCount = 1 }, imageExtent = new VkExtent3D { width = (uint)frameW, height = (uint)frameH, depth = 1 } };
            WithPtr(copy, p => vkCmdCopyBufferToImage(cmd, _staging[frame], srcImage, VkImageLayout.TRANSFER_DST_OPTIMAL, 1, p));
            Barrier(cmd, srcImage, VkImageLayout.TRANSFER_DST_OPTIMAL, VkImageLayout.TRANSFER_SRC_OPTIMAL, VkAccess.TRANSFER_WRITE, VkAccess.TRANSFER_READ, VkPipelineStage.TRANSFER, VkPipelineStage.TRANSFER, range);

            Barrier(cmd, swapImage, VkImageLayout.UNDEFINED, VkImageLayout.TRANSFER_DST_OPTIMAL, VkAccess.NONE, VkAccess.TRANSFER_WRITE, VkPipelineStage.TOP_OF_PIPE, VkPipelineStage.TRANSFER, range);
            var black = new VkClearColorValue { r = 0, g = 0, b = 0, a = 1 };
            var clearRange = range;
            vkCmdClearColorImage(cmd, swapImage, VkImageLayout.TRANSFER_DST_OPTIMAL, ref black, 1, ref clearRange);
            Barrier(cmd, swapImage, VkImageLayout.TRANSFER_DST_OPTIMAL, VkImageLayout.TRANSFER_DST_OPTIMAL, VkAccess.TRANSFER_WRITE, VkAccess.TRANSFER_WRITE, VkPipelineStage.TRANSFER, VkPipelineStage.TRANSFER, range);

            ComputeFitRect(frameW, frameH, (int)_scW, (int)_scH, out int dx0, out int dy0, out int dx1, out int dy1);
            var blit = new VkImageBlit
            {
                srcSubresource = new VkImageSubresourceLayers { aspectMask = VK_IMAGE_ASPECT_COLOR_BIT, layerCount = 1 },
                srcOffset1_x = frameW, srcOffset1_y = frameH, srcOffset1_z = 1,
                dstSubresource = new VkImageSubresourceLayers { aspectMask = VK_IMAGE_ASPECT_COLOR_BIT, layerCount = 1 },
                dstOffset0_x = dx0, dstOffset0_y = dy0, dstOffset1_x = dx1, dstOffset1_y = dy1, dstOffset1_z = 1,
            };
            // NEAREST, not LINEAR: retro 2D content must stay crisp (matches the old WriteableBitmap's
            // BitmapInterpolationMode.None). LINEAR bilinear-blurs the pixel-art upscale.
            WithPtr(blit, p => vkCmdBlitImage(cmd, srcImage, VkImageLayout.TRANSFER_SRC_OPTIMAL, swapImage, VkImageLayout.TRANSFER_DST_OPTIMAL, 1, p, VkFilter.NEAREST));

            Barrier(cmd, swapImage, VkImageLayout.TRANSFER_DST_OPTIMAL, VkImageLayout.PRESENT_SRC_KHR, VkAccess.TRANSFER_WRITE, VkAccess.NONE, VkPipelineStage.TRANSFER, VkPipelineStage.BOTTOM_OF_PIPE, range);
            vkEndCommandBuffer(cmd);

            IntPtr waitSem = Marshal.AllocHGlobal(IntPtr.Size), sigSem = Marshal.AllocHGlobal(IntPtr.Size), cmdPtr = Marshal.AllocHGlobal(IntPtr.Size), stagePtr = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                Marshal.WriteIntPtr(waitSem, _imgAvail[frame]);
                Marshal.WriteIntPtr(sigSem, _renderDone[idx]);
                Marshal.WriteIntPtr(cmdPtr, cmd);
                Marshal.WriteInt32(stagePtr, (int)VkPipelineStage.TRANSFER);
                var si = new VkSubmitInfo
                {
                    sType = VkStructureType.SUBMIT_INFO,
                    waitSemaphoreCount = 1, pWaitSemaphores = waitSem, pWaitDstStageMask = stagePtr,
                    commandBufferCount = 1, pCommandBuffers = cmdPtr,
                    signalSemaphoreCount = 1, pSignalSemaphores = sigSem,
                };
                Check(vkQueueSubmit(_queue, 1, ref si, _inFlight[frame]), "vkQueueSubmit");
            }
            finally { Marshal.FreeHGlobal(waitSem); Marshal.FreeHGlobal(sigSem); Marshal.FreeHGlobal(cmdPtr); Marshal.FreeHGlobal(stagePtr); }
        }

        private void Barrier(IntPtr cmd, IntPtr image, VkImageLayout oldL, VkImageLayout newL, VkAccess srcA, VkAccess dstA, VkPipelineStage srcS, VkPipelineStage dstS, VkImageSubresourceRange range)
        {
            var b = new VkImageMemoryBarrier
            {
                sType = VkStructureType.IMAGE_MEMORY_BARRIER, srcAccessMask = srcA, dstAccessMask = dstA,
                oldLayout = oldL, newLayout = newL, srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                image = image, subresourceRange = range,
            };
            WithPtr(b, p => vkCmdPipelineBarrier(cmd, srcS, dstS, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, 1, p));
        }

        private static void WithPtr<T>(T value, Action<IntPtr> use) where T : struct
        {
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
            try { Marshal.StructureToPtr(value, p, false); use(p); }
            finally { Marshal.FreeHGlobal(p); }
        }

        private static void ComputeFitRect(int sw, int sh, int dw, int dh, out int x0, out int y0, out int x1, out int y1)
        {
            double scale = Math.Min((double)dw / sw, (double)dh / sh);
            int w = (int)Math.Round(sw * scale), h = (int)Math.Round(sh * scale);
            x0 = (dw - w) / 2; y0 = (dh - h) / 2; x1 = x0 + w; y1 = y0 + h;
        }

        private static void Check(VkResult r, string what)
        {
            if (r != VkResult.VK_SUCCESS) throw new InvalidOperationException($"{what} failed: {r}");
        }

        public void Dispose()
        {
            if (_device != IntPtr.Zero) vkDeviceWaitIdle(_device);
            for (int i = 0; i < FRAMES; i++) DestroySrc(i);
            DestroySwapchain();
            if (_device != IntPtr.Zero)
            {
                for (int i = 0; i < FRAMES; i++)
                {
                    if (_imgAvail[i] != IntPtr.Zero) vkDestroySemaphore(_device, _imgAvail[i], IntPtr.Zero);
                    if (_inFlight[i] != IntPtr.Zero) vkDestroyFence(_device, _inFlight[i], IntPtr.Zero);
                }
                if (_cmdPool != IntPtr.Zero) vkDestroyCommandPool(_device, _cmdPool, IntPtr.Zero);
                vkDestroyDevice(_device, IntPtr.Zero);
                _device = IntPtr.Zero;
            }
            if (_surface != IntPtr.Zero && _destroySurface != null) _destroySurface(_instance, _surface, IntPtr.Zero);
            if (_instance != IntPtr.Zero) vkDestroyInstance(_instance, IntPtr.Zero);
            _instance = IntPtr.Zero;
        }

        private sealed class NativeStrArray : IDisposable
        {
            private readonly IntPtr[] _strs; private readonly IntPtr _arr;
            public IntPtr Ptr => _arr;
            public NativeStrArray(string[] items)
            {
                _strs = new IntPtr[items.Length];
                _arr = Marshal.AllocHGlobal(items.Length * IntPtr.Size);
                for (int i = 0; i < items.Length; i++) { _strs[i] = Marshal.StringToHGlobalAnsi(items[i]); Marshal.WriteIntPtr(_arr, i * IntPtr.Size, _strs[i]); }
            }
            public void Dispose()
            {
                foreach (var s in _strs) if (s != IntPtr.Zero) Marshal.FreeHGlobal(s);
                if (_arr != IntPtr.Zero) Marshal.FreeHGlobal(_arr);
            }
        }
    }
}
