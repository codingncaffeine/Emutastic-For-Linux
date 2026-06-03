using System;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Emutastic.Platform
{
    /// <summary>
    /// Embeds a native X11 child window inside the Avalonia visual tree (NativeControlHost) and exposes
    /// its XID so the emu thread can present a Vulkan swapchain into it (see <see cref="VulkanPresenter"/>).
    /// We don't create the child ourselves — we let Avalonia's X11 backend create + reparent + auto-track
    /// it (geometry follows the host automatically), and just capture the resulting XID. The Vulkan
    /// surface is built on a SEPARATE private Display connection on the emu thread (the XID is server-side,
    /// usable from any connection) so we never touch Avalonia's Display off the UI thread.
    /// </summary>
    public sealed class VkPresentHost : NativeControlHost
    {
        /// <summary>The native child window XID (0 until the control is realized). Descriptor "XID".</summary>
        public IntPtr ChildXid { get; private set; }

        /// <summary>Raised on the UI thread once <see cref="ChildXid"/> is available.</summary>
        public event Action? SurfaceReady;

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            var handle = base.CreateNativeControlCore(parent);   // Avalonia creates the child on its Display
            if (handle.HandleDescriptor == "XID" && handle.Handle != IntPtr.Zero)
            {
                ChildXid = handle.Handle;
                SurfaceReady?.Invoke();
            }
            return handle;
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            ChildXid = IntPtr.Zero;
            base.DestroyNativeControlCore(control);
        }
    }
}
