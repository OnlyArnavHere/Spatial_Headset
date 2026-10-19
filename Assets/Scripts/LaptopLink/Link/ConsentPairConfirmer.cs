using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SpatialLauncher.SpatialWindows;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// Asks the user to confirm a pairing with a pinch on the consent modal (<see cref="ConsentGate.Ask"/>). Called on a
    /// worker thread: the question is posted to the main thread and the answer completes a TaskCompletionSource there.
    /// No gate, a timeout or a cancellation count as No.
    /// </summary>
    public sealed class ConsentPairConfirmer : IPairConfirmer
    {
        readonly MainThreadQueue queue;

        public ConsentPairConfirmer(MainThreadQueue mainThread) { queue = mainThread; }

        /// <summary>The question: the laptop's name made safe for display, then its key fingerprint.</summary>
        public static string QuestionText(string laptopName, string fingerprint)
        {
            return "Pair with laptop " + DisplayName(laptopName) + " (" + fingerprint + ")?";
        }

        /// <summary>Control characters and markup brackets removed, at most 32 characters.</summary>
        public static string DisplayName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                if (sb.Length >= 32) break;
                if (char.IsControl(c) || char.IsSurrogate(c)) continue;
                sb.Append(c == '<' || c == '>' ? '?' : c);
            }
            return sb.Length == 0 ? "?" : sb.ToString();
        }

        public Task<bool> ConfirmAsync(string laptopName, string fingerprint, TimeSpan timeout, CancellationToken ct)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (queue == null || ct.IsCancellationRequested)
            {
                done.TrySetResult(false);
                return done.Task;
            }
            IRemoteSession token = null;
            string text = QuestionText(laptopName, fingerprint);
            queue.Enqueue(() =>
            {
                var gate = ConsentGate.Current;
                if (gate == null || done.Task.IsCompleted)
                {
                    done.TrySetResult(false);
                    return;
                }
                token = gate.Ask(text, (float)timeout.TotalSeconds, yes => done.TrySetResult(yes), "Pair laptop?");
            });
            ct.Register(() =>
            {
                done.TrySetResult(false);
                queue.Enqueue(() =>
                {
                    var gate = ConsentGate.Current;
                    if (token != null && gate != null) gate.Withdraw(token);
                });
            });
            return done.Task;
        }
    }
}
