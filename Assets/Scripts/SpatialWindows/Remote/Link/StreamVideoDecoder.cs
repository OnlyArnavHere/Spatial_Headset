using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Decodes one window's H.264 from the stream transport (USB without a network) into a texture, with the phone's
    /// hardware decoder (Assets/Plugins/iOS/SpatialVideoDecoder.mm). <see cref="Submit"/> may be called from the
    /// transport's reader thread; <see cref="Update"/> and <see cref="Dispose"/> run on the main thread. Outside an iOS
    /// player there is no decoder: <see cref="Supported"/> is false and frames are dropped (the stream transport only
    /// exists on the phone, where the USB tunnel ends).
    /// </summary>
    public sealed class StreamVideoDecoder : IDisposable
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern IntPtr SLVD_Create(int srgb);
        [DllImport("__Internal")] static extern int SLVD_Decode(IntPtr handle, IntPtr data, int length);
        [DllImport("__Internal")] static extern IntPtr SLVD_Acquire(IntPtr handle, out int width, out int height, out long serial);
        [DllImport("__Internal")] static extern void SLVD_Destroy(IntPtr handle);
        public static bool Supported => true;
#else
        static IntPtr SLVD_Create(int srgb) => IntPtr.Zero;
        static int SLVD_Decode(IntPtr handle, IntPtr data, int length) => 0;
        static IntPtr SLVD_Acquire(IntPtr handle, out int width, out int height, out long serial) { width = height = 0; serial = 0; return IntPtr.Zero; }
        static void SLVD_Destroy(IntPtr handle) { }
        public static bool Supported => false;
#endif

        readonly object gate = new object();
        readonly bool srgb;
        IntPtr handle;
        Texture2D texture;
        long shownSerial;
        bool disposed;

        public Texture Texture => texture;
        /// <summary>Access units handed to the decoder, and how many it refused (an OSStatus other than 0).</summary>
        public long Submitted { get; private set; }
        public long Failed { get; private set; }

        public StreamVideoDecoder()
        {
            // Linear rendering samples the frames as sRGB so they don't come out washed out.
            srgb = QualitySettings.activeColorSpace == ColorSpace.Linear;
            handle = SLVD_Create(srgb ? 1 : 0);
        }

        /// <summary>One access unit (Annex B). Any thread; decodes synchronously.</summary>
        public void Submit(ArraySegment<byte> annexB)
        {
            if (annexB.Array == null || annexB.Count <= 0) return;
            lock (gate)
            {
                if (disposed || handle == IntPtr.Zero) return;
                var pin = GCHandle.Alloc(annexB.Array, GCHandleType.Pinned);
                try
                {
                    int status = SLVD_Decode(handle, pin.AddrOfPinnedObject() + annexB.Offset, annexB.Count);
                    Submitted++;
                    if (status != 0) Failed++;
                }
                finally { pin.Free(); }
            }
        }

        /// <summary>Main thread: shows the newest decoded frame. True when <see cref="Texture"/> changed.</summary>
        public bool Update()
        {
            if (disposed || handle == IntPtr.Zero) return false;
            IntPtr native = SLVD_Acquire(handle, out int width, out int height, out long serial);
            if (native == IntPtr.Zero || serial == shownSerial || width <= 0 || height <= 0) return false;
            shownSerial = serial;
            if (texture == null || texture.width != width || texture.height != height)
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
                texture = Texture2D.CreateExternalTexture(width, height, TextureFormat.BGRA32, false, !srgb, native);
                texture.name = "Laptop window (USB)";
            }
            else texture.UpdateExternalTexture(native);
            return true;
        }

        /// <summary>Main thread.</summary>
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                if (handle != IntPtr.Zero) SLVD_Destroy(handle);
                handle = IntPtr.Zero;
            }
            if (texture != null) UnityEngine.Object.Destroy(texture);
            texture = null;
        }
    }
}
