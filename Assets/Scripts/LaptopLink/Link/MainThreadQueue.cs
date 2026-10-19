using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>Worker threads post actions here; the main thread runs them in <c>Update</c>. Not placed in a scene yet.</summary>
    public sealed class MainThreadQueue : MonoBehaviour
    {
        const int MaxPerFrame = 64;

        readonly ConcurrentQueue<Action> queue = new ConcurrentQueue<Action>();

        public void Enqueue(Action action)
        {
            if (action != null) queue.Enqueue(action);
        }

        /// <summary>Runs up to a frame's worth of queued actions; returns how many ran. A throwing action is logged and skipped.</summary>
        public int Pump()
        {
            int ran = 0;
            while (ran < MaxPerFrame && queue.TryDequeue(out var action))
            {
                ran++;
                try { action(); }
                catch (Exception e) { Debug.LogException(e); }
            }
            return ran;
        }

        void Update()
        {
            Pump();
        }
    }
}
