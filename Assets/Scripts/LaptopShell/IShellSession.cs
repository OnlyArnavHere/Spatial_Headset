using System;
using SpatialLauncher.SpatialWindows;

namespace SpatialLauncher.LaptopShell
{
    /// <summary>Why the Laptop Shell can't be used right now (None: it can).</summary>
    public enum ShellLock { None, Connecting, NeedsControl, Asking, NotOffered, Unsupported, CastOnly, InputOff, Stopped, Ended }

    /// <summary>
    /// What the Laptop Shell window talks to: the shell client and the session state around it. The real one is
    /// <see cref="LaptopShellLink"/> (one laptop session); tests and the UI gallery use a stand-in, so the window builds
    /// and is checked without WebRTC.
    /// </summary>
    public interface IShellSession : IDisposable
    {
        string LaptopName { get; }
        LaptopShellClient Client { get; }
        ShellLock Lock { get; }
        /// <summary>Asks the wearer, then the laptop, for control (the shell needs it). False when it can't be asked now.</summary>
        bool RequestControl();
        /// <summary>Every frame: timeouts, refreshes and the icon queue.</summary>
        void Tick();
        /// <summary>The shell's window, closed by a panic stop.</summary>
        SpatialWindow ShellWindow { get; set; }
    }

    public static class ShellLockText
    {
        /// <summary>What the locked shell says, for a laptop called <paramref name="laptop"/>.</summary>
        public static string Describe(ShellLock reason, string laptop)
        {
            string name = string.IsNullOrEmpty(laptop) ? "the laptop" : laptop;
            switch (reason)
            {
                case ShellLock.Connecting: return $"Connecting to {name}…";
                case ShellLock.NeedsControl:
                    return $"Allow control to search and open {name}'s apps. {name} asks you to confirm, unless you chose Always allow there.";
                case ShellLock.Asking: return "Waiting for approval…";
                case ShellLock.NotOffered: return $"{name}'s agent doesn't offer its apps. Update the agent on {name}.";
                case ShellLock.Unsupported: return $"Listing apps isn't supported on {name}'s system yet.";
                case ShellLock.CastOnly: return $"{name} shares media controls only, so its apps can't be opened from here.";
                case ShellLock.InputOff: return $"{name} doesn't allow control. Turn on Allow control in the agent there.";
                case ShellLock.Stopped: return $"Control was stopped on {name}. Restart the agent there to use its apps again.";
                case ShellLock.Ended: return $"The session with {name} ended. Connect again from Laptop.";
                default: return "";
            }
        }

        /// <summary>The lock offers an "Allow control" button.</summary>
        public static bool CanAsk(ShellLock reason) => reason == ShellLock.NeedsControl;
    }
}
