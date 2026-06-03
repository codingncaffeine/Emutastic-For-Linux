using System;
using System.Runtime.InteropServices;

namespace Emutastic.Platform
{
    /// <summary>
    /// Lean Vulkan P/Invoke surface for the Linux software-frame → FIFO-swapchain present path
    /// (see docs/frame-pacing-and-vsync.md). Ported from the upstream Windows VulkanInterop (struct
    /// layouts are platform-independent Vulkan ABI and reused verbatim) with three deltas:
    ///   • binds libvulkan.so.1 (not vulkan-1.dll),
    ///   • Xlib surface creation (VK_KHR_xlib_surface) instead of Win32,
    ///   • adds the image-create / linear-upload / blit functions the HW path didn't need.
    /// Only what <see cref="VulkanPresenter"/> uses is declared here.
    /// </summary>
    public static class Vk
    {
        public const string Lib = "libvulkan.so.1";

        // ---- enums ----
        public enum VkResult : int
        {
            VK_SUCCESS = 0, VK_NOT_READY = 1, VK_TIMEOUT = 2, VK_INCOMPLETE = 5,
            VK_SUBOPTIMAL_KHR = 1000001003,
            VK_ERROR_OUT_OF_DATE_KHR = -1000001004,
            VK_ERROR_INITIALIZATION_FAILED = -3, VK_ERROR_DEVICE_LOST = -4,
            VK_ERROR_EXTENSION_NOT_PRESENT = -7, VK_ERROR_INCOMPATIBLE_DRIVER = -9,
            VK_ERROR_SURFACE_LOST_KHR = -1000000000,
        }

        public enum VkStructureType : uint
        {
            APPLICATION_INFO = 0,
            INSTANCE_CREATE_INFO = 1,
            DEVICE_QUEUE_CREATE_INFO = 2,
            DEVICE_CREATE_INFO = 3,
            SUBMIT_INFO = 4,
            MEMORY_ALLOCATE_INFO = 5,
            FENCE_CREATE_INFO = 8,
            SEMAPHORE_CREATE_INFO = 9,
            BUFFER_CREATE_INFO = 12,
            IMAGE_CREATE_INFO = 14,
            COMMAND_POOL_CREATE_INFO = 39,
            COMMAND_BUFFER_ALLOCATE_INFO = 40,
            COMMAND_BUFFER_BEGIN_INFO = 42,
            IMAGE_MEMORY_BARRIER = 45,
            SWAPCHAIN_CREATE_INFO_KHR = 1000001000,
            PRESENT_INFO_KHR = 1000001001,
            XLIB_SURFACE_CREATE_INFO_KHR = 1000004000,
            PRESENT_ID_KHR = 1000294000,
            PHYS_DEV_PRESENT_ID_FEATURES_KHR = 1000294001,
            PHYS_DEV_PRESENT_WAIT_FEATURES_KHR = 1000248000,
        }

        public enum VkFormat : int
        {
            UNDEFINED = 0,
            R8G8B8A8_UNORM = 37,
            B8G8R8A8_UNORM = 44,
            B8G8R8A8_SRGB = 50,
        }

        public enum VkColorSpaceKHR : int { SRGB_NONLINEAR_KHR = 0 }

        public enum VkPresentModeKHR : int
        {
            IMMEDIATE = 0, MAILBOX = 1, FIFO = 2, FIFO_RELAXED = 3,
        }

        public enum VkImageLayout : uint
        {
            UNDEFINED = 0, GENERAL = 1,
            TRANSFER_SRC_OPTIMAL = 6, TRANSFER_DST_OPTIMAL = 7,
            PRESENT_SRC_KHR = 1000001002,
        }

        public enum VkImageType : uint { T2D = 1 }
        public enum VkImageTiling : uint { OPTIMAL = 0, LINEAR = 1 }
        public enum VkSharingMode : uint { EXCLUSIVE = 0 }
        public enum VkFilter : uint { NEAREST = 0, LINEAR = 1 }

        [Flags] public enum VkImageUsage : uint
        {
            TRANSFER_SRC = 0x1, TRANSFER_DST = 0x4, COLOR_ATTACHMENT = 0x10,
        }
        [Flags] public enum VkBufferUsage : uint { TRANSFER_SRC = 0x1, TRANSFER_DST = 0x2 }
        [Flags] public enum VkQueueFlags : uint { GRAPHICS = 0x1 }
        [Flags] public enum VkMemoryProperty : uint
        {
            DEVICE_LOCAL = 0x1, HOST_VISIBLE = 0x2, HOST_COHERENT = 0x4,
        }
        [Flags] public enum VkCommandPoolCreate : uint { RESET_COMMAND_BUFFER = 0x2 }
        [Flags] public enum VkAccess : uint
        {
            NONE = 0, TRANSFER_READ = 0x800, TRANSFER_WRITE = 0x1000,
            HOST_WRITE = 0x4000, MEMORY_READ = 0x8000,
        }
        [Flags] public enum VkPipelineStage : uint
        {
            TOP_OF_PIPE = 0x1, TRANSFER = 0x1000, HOST = 0x4000, BOTTOM_OF_PIPE = 0x2000,
            ALL_COMMANDS = 0x10000,
        }
        public const uint VK_IMAGE_ASPECT_COLOR_BIT = 0x1;
        public const uint VK_SAMPLE_COUNT_1_BIT = 0x1;
        public const uint VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR = 0x1;
        public const uint VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT = 0x1;
        public const uint VK_QUEUE_FAMILY_IGNORED = 0xFFFFFFFF;
        public const ulong UINT64_MAX = 0xFFFFFFFFFFFFFFFFUL;

        // ---- structs ----
        [StructLayout(LayoutKind.Sequential)]
        public struct VkApplicationInfo
        {
            public VkStructureType sType; public IntPtr pNext;
            public IntPtr pApplicationName; public uint applicationVersion;
            public IntPtr pEngineName; public uint engineVersion; public uint apiVersion;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkInstanceCreateInfo
        {
            public VkStructureType sType; public IntPtr pNext; public uint flags;
            public IntPtr pApplicationInfo;
            public uint enabledLayerCount; public IntPtr ppEnabledLayerNames;
            public uint enabledExtensionCount; public IntPtr ppEnabledExtensionNames;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkDeviceQueueCreateInfo
        {
            public VkStructureType sType; public IntPtr pNext; public uint flags;
            public uint queueFamilyIndex; public uint queueCount; public IntPtr pQueuePriorities;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkDeviceCreateInfo
        {
            public VkStructureType sType; public IntPtr pNext; public uint flags;
            public uint queueCreateInfoCount; public IntPtr pQueueCreateInfos;
            public uint enabledLayerCount; public IntPtr ppEnabledLayerNames;
            public uint enabledExtensionCount; public IntPtr ppEnabledExtensionNames;
            public IntPtr pEnabledFeatures;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkExtent2D { public uint width, height; }
        [StructLayout(LayoutKind.Sequential)]
        public struct VkExtent3D { public uint width, height, depth; }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkQueueFamilyProperties
        {
            public VkQueueFlags queueFlags; public uint queueCount;
            public uint timestampValidBits; public VkExtent3D minImageTransferGranularity;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkXlibSurfaceCreateInfoKHR
        {
            public VkStructureType sType; public IntPtr pNext; public uint flags;
            public IntPtr dpy;      // Display*
            public IntPtr window;   // Window (XID; unsigned long on LP64)
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkSurfaceCapabilitiesKHR
        {
            public uint minImageCount, maxImageCount;
            public VkExtent2D currentExtent, minImageExtent, maxImageExtent;
            public uint maxImageArrayLayers, supportedTransforms, currentTransform;
            public uint supportedCompositeAlpha, supportedUsageFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkSurfaceFormatKHR { public VkFormat format; public VkColorSpaceKHR colorSpace; }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkSwapchainCreateInfoKHR
        {
            public VkStructureType sType; public IntPtr pNext; public uint flags;
            public IntPtr surface;          // VkSurfaceKHR
            public uint minImageCount;
            public VkFormat imageFormat; public VkColorSpaceKHR imageColorSpace;
            public VkExtent2D imageExtent; public uint imageArrayLayers;
            public VkImageUsage imageUsage; public VkSharingMode imageSharingMode;
            public uint queueFamilyIndexCount; public IntPtr pQueueFamilyIndices;
            public uint preTransform; public uint compositeAlpha;
            public VkPresentModeKHR presentMode; public uint clipped;
            public IntPtr oldSwapchain;     // VkSwapchainKHR
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkPresentInfoKHR
        {
            public VkStructureType sType; public IntPtr pNext;
            public uint waitSemaphoreCount; public IntPtr pWaitSemaphores;
            public uint swapchainCount; public IntPtr pSwapchains;
            public IntPtr pImageIndices; public IntPtr pResults;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkImageCreateInfo
        {
            public VkStructureType sType; public IntPtr pNext; public uint flags;
            public VkImageType imageType; public VkFormat format; public VkExtent3D extent;
            public uint mipLevels; public uint arrayLayers; public uint samples;
            public VkImageTiling tiling; public VkImageUsage usage; public VkSharingMode sharingMode;
            public uint queueFamilyIndexCount; public IntPtr pQueueFamilyIndices;
            public VkImageLayout initialLayout;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkMemoryRequirements { public ulong size, alignment; public uint memoryTypeBits; }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkMemoryAllocateInfo
        {
            public VkStructureType sType; public IntPtr pNext;
            public ulong allocationSize; public uint memoryTypeIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkImageSubresource { public uint aspectMask, mipLevel, arrayLayer; }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkSubresourceLayout { public ulong offset, size, rowPitch, arrayPitch, depthPitch; }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkMemoryType { public VkMemoryProperty propertyFlags; public uint heapIndex; }

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct VkPhysicalDeviceMemoryProperties
        {
            public uint memoryTypeCount;
            public fixed byte memoryTypes[32 * 8];   // 32 × VkMemoryType (8 bytes)
            public uint memoryHeapCount;
            public fixed byte memoryHeaps[16 * 16];
            public VkMemoryType GetMemoryType(int i)
            {
                if ((uint)i >= memoryTypeCount || i >= 32) throw new ArgumentOutOfRangeException(nameof(i));
                fixed (byte* p = memoryTypes) return ((VkMemoryType*)p)[i];
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkCommandPoolCreateInfo
        {
            public VkStructureType sType; public IntPtr pNext;
            public VkCommandPoolCreate flags; public uint queueFamilyIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkCommandBufferAllocateInfo
        {
            public VkStructureType sType; public IntPtr pNext;
            public IntPtr commandPool; public uint level; public uint commandBufferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkCommandBufferBeginInfo
        {
            public VkStructureType sType; public IntPtr pNext; public uint flags; public IntPtr pInheritanceInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkImageSubresourceRange
        {
            public uint aspectMask, baseMipLevel, levelCount, baseArrayLayer, layerCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkImageMemoryBarrier
        {
            public VkStructureType sType; public IntPtr pNext;
            public VkAccess srcAccessMask, dstAccessMask;
            public VkImageLayout oldLayout, newLayout;
            public uint srcQueueFamilyIndex, dstQueueFamilyIndex;
            public IntPtr image;            // VkImage
            public VkImageSubresourceRange subresourceRange;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkImageSubresourceLayers
        {
            public uint aspectMask, mipLevel, baseArrayLayer, layerCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkImageBlit
        {
            public VkImageSubresourceLayers srcSubresource;
            public int srcOffset0_x, srcOffset0_y, srcOffset0_z;
            public int srcOffset1_x, srcOffset1_y, srcOffset1_z;
            public VkImageSubresourceLayers dstSubresource;
            public int dstOffset0_x, dstOffset0_y, dstOffset0_z;
            public int dstOffset1_x, dstOffset1_y, dstOffset1_z;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkSubmitInfo
        {
            public VkStructureType sType; public IntPtr pNext;
            public uint waitSemaphoreCount; public IntPtr pWaitSemaphores; public IntPtr pWaitDstStageMask;
            public uint commandBufferCount; public IntPtr pCommandBuffers;
            public uint signalSemaphoreCount; public IntPtr pSignalSemaphores;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkSemaphoreCreateInfo { public VkStructureType sType; public IntPtr pNext; public uint flags; }
        [StructLayout(LayoutKind.Sequential)]
        public struct VkFenceCreateInfo { public VkStructureType sType; public IntPtr pNext; public uint flags; }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkClearColorValue { public float r, g, b, a; }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkBufferCreateInfo
        {
            public VkStructureType sType; public IntPtr pNext; public uint flags;
            public ulong size; public VkBufferUsage usage; public VkSharingMode sharingMode;
            public uint queueFamilyIndexCount; public IntPtr pQueueFamilyIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkOffset3D { public int x, y, z; }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkBufferImageCopy
        {
            public ulong bufferOffset; public uint bufferRowLength; public uint bufferImageHeight;
            public VkImageSubresourceLayers imageSubresource;
            public VkOffset3D imageOffset; public VkExtent3D imageExtent;
        }

        // VK_KHR_present_id / VK_KHR_present_wait — lockstep production to ACTUAL present completion.
        [StructLayout(LayoutKind.Sequential)]
        public struct VkPresentIdKHR
        {
            public VkStructureType sType; public IntPtr pNext; public uint swapchainCount; public IntPtr pPresentIds;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct VkPhysicalDevicePresentIdFeaturesKHR
        {
            public VkStructureType sType; public IntPtr pNext; public uint presentId;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct VkPhysicalDevicePresentWaitFeaturesKHR
        {
            public VkStructureType sType; public IntPtr pNext; public uint presentWait;
        }

        // ---- core entry points (statically linked) ----
        const CallingConvention CC = CallingConvention.Cdecl;
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkCreateInstance(ref VkInstanceCreateInfo ci, IntPtr alloc, out IntPtr instance);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkDestroyInstance(IntPtr instance, IntPtr alloc);
        [DllImport(Lib, CallingConvention = CC)] public static extern IntPtr vkGetInstanceProcAddr(IntPtr instance, [MarshalAs(UnmanagedType.LPStr)] string name);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr devices);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkGetPhysicalDeviceQueueFamilyProperties(IntPtr gpu, ref uint count, IntPtr props);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkGetPhysicalDeviceMemoryProperties(IntPtr gpu, out VkPhysicalDeviceMemoryProperties props);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkCreateDevice(IntPtr gpu, ref VkDeviceCreateInfo ci, IntPtr alloc, out IntPtr device);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkDestroyDevice(IntPtr device, IntPtr alloc);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkGetDeviceQueue(IntPtr device, uint qf, uint qi, out IntPtr queue);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkDeviceWaitIdle(IntPtr device);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkQueueWaitIdle(IntPtr queue);

        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkCreateSwapchainKHR(IntPtr device, ref VkSwapchainCreateInfoKHR ci, IntPtr alloc, out IntPtr swapchain);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkDestroySwapchainKHR(IntPtr device, IntPtr swapchain, IntPtr alloc);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkGetSwapchainImagesKHR(IntPtr device, IntPtr swapchain, ref uint count, IntPtr images);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkAcquireNextImageKHR(IntPtr device, IntPtr swapchain, ulong timeout, IntPtr semaphore, IntPtr fence, out uint imageIndex);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkQueuePresentKHR(IntPtr queue, ref VkPresentInfoKHR pi);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkWaitForPresentKHR(IntPtr device, IntPtr swapchain, ulong presentId, ulong timeout);

        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkCreateImage(IntPtr device, ref VkImageCreateInfo ci, IntPtr alloc, out IntPtr image);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkDestroyImage(IntPtr device, IntPtr image, IntPtr alloc);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkGetImageMemoryRequirements(IntPtr device, IntPtr image, out VkMemoryRequirements req);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkBindImageMemory(IntPtr device, IntPtr image, IntPtr memory, ulong offset);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkGetImageSubresourceLayout(IntPtr device, IntPtr image, ref VkImageSubresource sub, out VkSubresourceLayout layout);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkCreateBuffer(IntPtr device, ref VkBufferCreateInfo ci, IntPtr alloc, out IntPtr buffer);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkDestroyBuffer(IntPtr device, IntPtr buffer, IntPtr alloc);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkGetBufferMemoryRequirements(IntPtr device, IntPtr buffer, out VkMemoryRequirements req);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkBindBufferMemory(IntPtr device, IntPtr buffer, IntPtr memory, ulong offset);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkCmdCopyBufferToImage(IntPtr cmd, IntPtr srcBuffer, IntPtr dstImage, VkImageLayout dstLayout, uint regionCount, IntPtr regions);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkAllocateMemory(IntPtr device, ref VkMemoryAllocateInfo ai, IntPtr alloc, out IntPtr memory);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkFreeMemory(IntPtr device, IntPtr memory, IntPtr alloc);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkMapMemory(IntPtr device, IntPtr memory, ulong offset, ulong size, uint flags, out IntPtr data);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkUnmapMemory(IntPtr device, IntPtr memory);

        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkCreateCommandPool(IntPtr device, ref VkCommandPoolCreateInfo ci, IntPtr alloc, out IntPtr pool);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkDestroyCommandPool(IntPtr device, IntPtr pool, IntPtr alloc);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkAllocateCommandBuffers(IntPtr device, ref VkCommandBufferAllocateInfo ai, out IntPtr cmd);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkBeginCommandBuffer(IntPtr cmd, ref VkCommandBufferBeginInfo bi);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkEndCommandBuffer(IntPtr cmd);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkResetCommandBuffer(IntPtr cmd, uint flags);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkCmdPipelineBarrier(IntPtr cmd, VkPipelineStage src, VkPipelineStage dst, uint depFlags, uint mbc, IntPtr mb, uint bbc, IntPtr bb, uint ibc, IntPtr ib);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkCmdBlitImage(IntPtr cmd, IntPtr srcImage, VkImageLayout srcLayout, IntPtr dstImage, VkImageLayout dstLayout, uint regionCount, IntPtr regions, VkFilter filter);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkCmdClearColorImage(IntPtr cmd, IntPtr image, VkImageLayout layout, ref VkClearColorValue color, uint rangeCount, ref VkImageSubresourceRange ranges);

        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkQueueSubmit(IntPtr queue, uint count, ref VkSubmitInfo si, IntPtr fence);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkCreateSemaphore(IntPtr device, ref VkSemaphoreCreateInfo ci, IntPtr alloc, out IntPtr sem);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkDestroySemaphore(IntPtr device, IntPtr sem, IntPtr alloc);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkCreateFence(IntPtr device, ref VkFenceCreateInfo ci, IntPtr alloc, out IntPtr fence);
        [DllImport(Lib, CallingConvention = CC)] public static extern void vkDestroyFence(IntPtr device, IntPtr fence, IntPtr alloc);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkWaitForFences(IntPtr device, uint count, ref IntPtr fences, uint waitAll, ulong timeout);
        [DllImport(Lib, CallingConvention = CC)] public static extern VkResult vkResetFences(IntPtr device, uint count, ref IntPtr fences);

        // ---- KHR surface entry points (loaded via vkGetInstanceProcAddr) ----
        [UnmanagedFunctionPointer(CC)] public delegate VkResult PFN_CreateXlibSurfaceKHR(IntPtr instance, ref VkXlibSurfaceCreateInfoKHR ci, IntPtr alloc, out IntPtr surface);
        [UnmanagedFunctionPointer(CC)] public delegate void PFN_DestroySurfaceKHR(IntPtr instance, IntPtr surface, IntPtr alloc);
        [UnmanagedFunctionPointer(CC)] public delegate VkResult PFN_GetPhysicalDeviceSurfaceSupportKHR(IntPtr gpu, uint qf, IntPtr surface, out uint supported);
        [UnmanagedFunctionPointer(CC)] public delegate VkResult PFN_GetPhysicalDeviceSurfaceCapabilitiesKHR(IntPtr gpu, IntPtr surface, out VkSurfaceCapabilitiesKHR caps);
        [UnmanagedFunctionPointer(CC)] public delegate VkResult PFN_GetPhysicalDeviceSurfaceFormatsKHR(IntPtr gpu, IntPtr surface, ref uint count, IntPtr formats);
        [UnmanagedFunctionPointer(CC)] public delegate VkResult PFN_GetPhysicalDeviceSurfacePresentModesKHR(IntPtr gpu, IntPtr surface, ref uint count, IntPtr modes);

        public static T Load<T>(IntPtr instance, string name) where T : Delegate
        {
            IntPtr p = vkGetInstanceProcAddr(instance, name);
            if (p == IntPtr.Zero) throw new InvalidOperationException($"vkGetInstanceProcAddr({name}) returned null");
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        public static bool IsAvailable()
        {
            try
            {
                if (!NativeLibrary.TryLoad(Lib, out var h)) return false;
                NativeLibrary.Free(h);
                return true;
            }
            catch { return false; }
        }

        public static uint MakeVersion(uint major, uint minor, uint patch) => (major << 22) | (minor << 12) | patch;
    }
}
