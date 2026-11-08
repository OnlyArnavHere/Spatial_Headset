using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Looks for QR codes in the passthrough camera while the pairing screen is open, so the headset can pair by looking
    /// at the laptop's QR (pair_protocol.py, "QR pairing"). Four times a second it takes the newest camera frame as a
    /// half-size grayscale image (<see cref="ARCameraManager.TryAcquireLatestCpuImage"/>) and reads it on a worker thread
    /// with Apple's Vision framework (Assets/Plugins/iOS/SpatialQrScanner.mm). <see cref="Found"/> is raised on the main
    /// thread from <see cref="Tick"/> with each QR's text. Outside an iOS player there is no reader
    /// (<see cref="Supported"/> is false); <see cref="Submit"/> stands in for the camera in tests.
    /// </summary>
    public sealed class PairQrScanner : IDisposable
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int SLQR_Detect(byte[] gray, int width, int height, int rowBytes, byte[] output, int outputLength);
        public static bool Supported => true;
#else
        static int SLQR_Detect(byte[] gray, int width, int height, int rowBytes, byte[] output, int outputLength) => 0;
        public static bool Supported => false;
#endif

        public const float Interval = 0.25f;
        const int MaxWidth = 1280;

        public event Action<string> Found;
        /// <summary>Frames handed to the reader so far.</summary>
        public int Scans { get; private set; }

        ARCameraManager cameraManager;
        float next;
        Task<string> pending;
        byte[] frame;
        bool disposed;

        /// <summary>Main thread, every frame while scanning.</summary>
        public void Tick(float now)
        {
            if (disposed) return;
            if (pending != null)
            {
                if (!pending.IsCompleted) return;
                string text = pending.Status == TaskStatus.RanToCompletion ? pending.Result : null;
                pending = null;
                if (!string.IsNullOrEmpty(text)) Found?.Invoke(text);
            }
            if (!Supported || now < next) return;
            next = now + Interval;
            if (cameraManager == null) cameraManager = UnityEngine.Object.FindFirstObjectByType<ARCameraManager>();
            if (cameraManager == null || !cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image)) return;
            int width, height, rowBytes;
            try
            {
                int scale = image.width > MaxWidth ? 2 : 1;
                var conversion = new XRCpuImage.ConversionParams(image, TextureFormat.R8, XRCpuImage.Transformation.None)
                {
                    outputDimensions = new Vector2Int(image.width / scale, image.height / scale),
                };
                int size = image.GetConvertedDataSize(conversion);
                width = conversion.outputDimensions.x;
                height = conversion.outputDimensions.y;
                rowBytes = size / height;
                if (frame == null || frame.Length != size) frame = new byte[size];
                using (var native = new NativeArray<byte>(size, Allocator.TempJob, NativeArrayOptions.UninitializedMemory))
                {
                    image.Convert(conversion, new NativeSlice<byte>(native));
                    native.CopyTo(frame);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Pair] can't read the camera for the QR code: " + e.Message);
                return;
            }
            finally
            {
                image.Dispose();
            }
            byte[] gray = frame; // not touched again until this read finishes (pending blocks the next frame)
            Scans++;
            pending = Task.Run(() => Detect(gray, width, height, rowBytes));
        }

        /// <summary>Hands a QR's text in as if the camera had read it (tests, and the editor).</summary>
        public void Submit(string text)
        {
            if (!disposed && !string.IsNullOrEmpty(text)) Found?.Invoke(text);
        }

        static string Detect(byte[] gray, int width, int height, int rowBytes)
        {
            var output = new byte[512];
            int length = SLQR_Detect(gray, width, height, rowBytes, output, output.Length);
            return length > 0 ? Encoding.UTF8.GetString(output, 0, length) : null;
        }

        public void Dispose()
        {
            disposed = true;
            Found = null;
        }
    }
}
