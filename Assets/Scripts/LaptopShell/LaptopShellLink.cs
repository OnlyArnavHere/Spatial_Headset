#if SPATIAL_LAUNCHER_WEBRTC
using System;
using System.Collections.Generic;
using SpatialLauncher.SpatialWindows;

namespace SpatialLauncher.LaptopShell
{
    /// <summary>
    /// Binds a <see cref="LaptopShellClient"/> to one laptop session: shell requests go out on the control channel only
    /// while the session holds granted control, replies come back by rid, and a window the shell opens becomes a stream
    /// window on a slot of the same session (through <see cref="OpenLinked"/>, which the launcher sets, like the picker's).
    /// Control is asked for through the ConsentGate with "full control" (<see cref="RequestControl"/>), the same prompt a
    /// stream window uses, so the laptop's own approval (or its always-allow) and the LIVE indicator apply unchanged.
    /// Call <see cref="Tick"/> every frame; <see cref="Dispose"/> unhooks everything.
    /// </summary>
    public sealed class LaptopShellLink : IRemoteSession, IDisposable
    {
        readonly HashSet<string> requestedOpens = new HashSet<string>();
        readonly Dictionary<string, string> titles = new Dictionary<string, string>();
        bool asking;

        public LinkSession Session { get; }
        public LaptopShellClient Client { get; }
        /// <summary>Opens the stream window for an opened target (set by the launcher).</summary>
        public Action<LinkSession, OpenedWindow, LaptopTarget> OpenLinked;
        /// <summary>The shell's own window, closed by a panic stop (set by the shell window).</summary>
        public SpatialWindow ShellWindow { get; set; }
        /// <summary>Waiting for the wearer's answer on the consent prompt.</summary>
        public bool AwaitingConsent => asking;
        public bool IsDisposed { get; private set; }

        public string SourceName => Session.LaptopName;
        public string Address => Session.Address;
        public string ModeLabel => "full control";
        public SpatialWindow Window => ShellWindow;
        public void StopForPanic() => Session.StopForPanic();

        public LaptopShellLink(LinkSession session, LaptopShellClient client = null)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Client = client ?? new LaptopShellClient(json => session.Control.SendShell(json));
            var control = session.Control;
            control.StateChanged += Sync;
            control.Welcomed += Sync;
            control.ShellReceived += Client.Receive;
            control.Opened += OnOpened;
            control.OpenFailed += OnOpenFailed;
            session.Ended += OnEnded;
            Client.StreamRequested += OnStreamRequested;
            Sync();
        }

        void Sync()
        {
            if (IsDisposed) return;
            var control = Session.Control;
            Client.SetAvailable(control.ShellUsable, LaptopShellClient.ParseOs(control.ShellOs));
        }

        /// <summary>
        /// Asks the wearer (ConsentGate, "full control") and then the laptop for control, so the shell can be used.
        /// False when it can't be asked now (already in control, not view only, no gate).
        /// </summary>
        public bool RequestControl(ConsentGate gate)
        {
            var control = Session.Control;
            if (IsDisposed || gate == null || asking || control.State != LinkSessionState.ViewOnly) return false;
            asking = true;
            gate.Request(this, () =>
            {
                asking = false;
                if (IsDisposed || Session.IsEnded) return;
                Session.Control.ApproveControlFromConsent(); // the approve callback: the only place that may
            }, reason => asking = false);
            return true;
        }

        void OnStreamRequested(string windowId, string title)
        {
            if (IsDisposed || Session.IsEnded) return;
            requestedOpens.Add(windowId);
            titles[windowId] = title;
            Session.Control.Open(windowId);
        }

        void OnOpened(OpenedWindow opened)
        {
            if (IsDisposed || opened == null || !requestedOpens.Remove(opened.Id)) return; // asked for by a picker
            titles.TryGetValue(opened.Id, out var title);
            titles.Remove(opened.Id);
            var target = new LaptopTarget
            {
                id = opened.Id, path = opened.Path, kind = opened.Kind ?? "window",
                title = string.IsNullOrEmpty(title) ? opened.Description : title, description = opened.Description,
                width = opened.Width, height = opened.Height,
            };
            OpenLinked?.Invoke(Session, opened, target);
        }

        void OnOpenFailed(string reason)
        {
            if (IsDisposed || requestedOpens.Count == 0) return;
            // The agent doesn't say which open failed; opens are one at a time from a tap, so it is the oldest.
            foreach (var id in requestedOpens)
            {
                requestedOpens.Remove(id);
                titles.Remove(id);
                break;
            }
        }

        void OnEnded(string reason)
        {
            Client.Reset();
            Dispose();
        }

        public void Tick()
        {
            if (!IsDisposed) Client.Tick();
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            var control = Session.Control;
            control.StateChanged -= Sync;
            control.Welcomed -= Sync;
            control.ShellReceived -= Client.Receive;
            control.Opened -= OnOpened;
            control.OpenFailed -= OnOpenFailed;
            Session.Ended -= OnEnded;
            Client.StreamRequested -= OnStreamRequested;
            if (asking) ConsentGate.Current?.Withdraw(this);
            asking = false;
            Client.SetAvailable(false, Client.Os);
        }
    }
}
#endif
