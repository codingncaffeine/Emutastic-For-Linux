using System;
using System.Runtime.InteropServices;

namespace Emutastic.Platform
{
    /// <summary>
    /// Vulkan hardware rendering for libretro cores (RETRO_HW_CONTEXT_VULKAN), in libwlpresent's wl_hwvk.c. The core
    /// makes its device on our instance through its context negotiation; each frame it hands over as a VkImage is
    /// copied on the GPU into memory the own-toplevel window's GL context imports, so a large frame (an Xbox game at
    /// 6x is 3840x2880) never crosses the CPU. ReadBack gives the CPU the frame where that cannot work (another GPU,
    /// no import extension, the recorder, the in-process presenters).
    /// </summary>
    internal static class WlVulkan
    {
        const string LIB = "wlpresent";   // resolved by WlToplevelPresenter's DllImportResolver (same assembly)

        static WlVulkan() { System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(WlToplevelPresenter).TypeHandle); }

        [DllImport(LIB)] static extern IntPtr wlvk_create(IntPtr negotiation);
        [DllImport(LIB)] static extern IntPtr wlvk_interface(IntPtr vk);
        [DllImport(LIB)] static extern int wlvk_frame(IntPtr vk, int w, int hh);
        [DllImport(LIB)] static extern int wlvk_readback(IntPtr vk, IntPtr xrgb, int maxBytes, ref int w, ref int hh);
        [DllImport(LIB)] static extern void wlvk_destroy(IntPtr vk);

        /// <summary>Runs the core's negotiation (IntPtr.Zero: none) on a new instance. Emu thread. Zero on failure.</summary>
        public static IntPtr Create(IntPtr negotiation)
        {
            try { return wlvk_create(negotiation); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Emu] Vulkan HW render unavailable: {ex.Message}"); return IntPtr.Zero; }
        }
        /// <summary>The retro_hw_render_interface_vulkan for GET_HW_RENDER_INTERFACE.</summary>
        public static IntPtr Interface(IntPtr vk) => wlvk_interface(vk);
        /// <summary>This frame's handed-over image into a slot for the window. Emu thread.</summary>
        public static bool Frame(IntPtr vk, int w, int h) => wlvk_frame(vk, w, h) != 0;
        /// <summary>This frame's image to the CPU as XRGB8888 rows into dst (w x h). Emu thread.</summary>
        public static bool ReadBack(IntPtr vk, IntPtr dst, int maxBytes, int w, int h) => wlvk_readback(vk, dst, maxBytes, ref w, ref h) != 0;
        /// <summary>After the core's context_destroy: the device and the instance go.</summary>
        public static void Destroy(IntPtr vk) { if (vk != IntPtr.Zero) wlvk_destroy(vk); }
    }
}
