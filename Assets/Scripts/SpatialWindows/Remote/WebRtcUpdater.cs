#if SPATIAL_LAUNCHER_WEBRTC
using Unity.WebRTC;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Runs Unity WebRTC's per-frame texture update (<see cref="WebRTC.Update"/>) exactly once while any stream needs it.
    /// Two copies of that coroutine would update every video texture twice per frame.
    /// </summary>
    sealed class WebRtcUpdater : MonoBehaviour
    {
        static WebRtcUpdater instance;
        static int users;

        /// <summary>
        /// False after scripts were reloaded during Play mode in the editor. Unity WebRTC sets up its main-thread
        /// dispatcher only when Play mode starts, so after such a reload every native callback fails and closing a
        /// connection can hang the editor. Streams refuse to start until Play mode is restarted.
        /// </summary>
        public static bool IsUsable { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)] // same moment WebRTC sets up its dispatcher
        static void MarkPlaySessionStart() => IsUsable = true;

        public static void Acquire()
        {
            users++;
            if (instance != null) return;
            var go = new GameObject("WebRTC Updater") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            instance = go.AddComponent<WebRtcUpdater>();
            instance.StartCoroutine(WebRTC.Update());
        }

        public static void Release()
        {
            users = Mathf.Max(0, users - 1);
            if (users > 0 || instance == null) return;
            Destroy(instance.gameObject);
            instance = null;
        }
    }
}
#endif
