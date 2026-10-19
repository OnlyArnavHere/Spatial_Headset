using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>Token bucket per source: <c>attempts</c> per <c>window</c> seconds (5 per 60 s). Thread safe; the clock returns seconds.</summary>
    public sealed class RateLimiter
    {
        public const int DefaultAttempts = 5;
        public const double DefaultWindow = 60.0;

        readonly object gate = new object();
        readonly Func<double> clock;
        readonly double cap;
        readonly double rate;
        Dictionary<string, double[]> buckets = new Dictionary<string, double[]>(); // tokens, last

        public RateLimiter(Func<double> secondsClock = null, int attempts = DefaultAttempts, double window = DefaultWindow)
        {
            clock = secondsClock ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
            cap = attempts;
            rate = attempts / window;
        }

        public bool Allow(string source)
        {
            lock (gate)
            {
                double now = clock();
                double tokens = cap, last = now;
                if (buckets.TryGetValue(source, out var b))
                {
                    tokens = b[0];
                    last = b[1];
                }
                tokens = Math.Min(cap, tokens + (now - last) * rate);
                bool allowed = tokens >= 1.0;
                if (allowed) tokens -= 1.0;
                buckets[source] = new[] { tokens, now };
                if (buckets.Count > 1024) // forget sources that have fully recovered
                {
                    var kept = new Dictionary<string, double[]>();
                    foreach (var kv in buckets)
                        if (Math.Min(cap, kv.Value[0] + (now - kv.Value[1]) * rate) < cap) kept[kv.Key] = kv.Value;
                    buckets = kept;
                }
                return allowed;
            }
        }
    }

    /// <summary>
    /// Pre-authentication limits per source IP: the rate limit and one exchange at a time. A refused source is dropped
    /// without a reply. Every successful <see cref="TryEnter"/> must be paired with <see cref="Leave"/>.
    /// </summary>
    public sealed class HeadsetGate
    {
        readonly object gate = new object();
        readonly RateLimiter rate;
        readonly HashSet<string> busy = new HashSet<string>();

        public HeadsetGate(Func<double> secondsClock = null, int attempts = RateLimiter.DefaultAttempts, double window = RateLimiter.DefaultWindow)
        {
            rate = new RateLimiter(secondsClock, attempts, window);
        }

        public bool TryEnter(string source)
        {
            lock (gate)
            {
                if (busy.Contains(source) || !rate.Allow(source)) return false;
                busy.Add(source);
                return true;
            }
        }

        public void Leave(string source)
        {
            lock (gate) busy.Remove(source);
        }
    }
}
