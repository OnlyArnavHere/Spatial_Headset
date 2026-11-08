#if SPATIAL_LAUNCHER_WEBRTC
using System;
using System.Collections.Generic;
using System.Text;
using SpatialLauncher.LaptopLink;
using Unity.WebRTC;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// The headset's one WebRTC session with a laptop agent: the peer connection, the "control" channel (id 0) and one
    /// "input-k" channel (id 1+k) and video slot per m-line of the offer. Owns all of them and the WebRtcUpdater
    /// reference; <see cref="Dispose"/> (also <see cref="StopForPanic"/>) closes everything and gives the listener lease
    /// back. All pc, channel and callback work runs on the main thread (Unity WebRTC raises callbacks there); the message
    /// handlers only parse and update state. The owner calls <see cref="Tick"/> every frame once started.
    /// The session ends on pc Failed/Closed, after more than 10 s Disconnected, or when the control client ends it (an
    /// old agent, a stop, an oversize message); the teardown itself happens in the next <see cref="Tick"/>, never inside
    /// a WebRTC callback.
    /// Over the stream transport (USB without a network; <see cref="StreamLinkTransport"/>) there is no peer connection:
    /// the handshake's own connection carries the same control and input messages, each slot decodes its own video
    /// (<see cref="SlotVideoSource.UseDecoder"/>), and the session ends when that connection closes.
    /// </summary>
    public sealed class LinkSession : IDisposable
    {
        const float DisconnectedLimit = 10f;
        const float FlushLimit = 0.3f;

        readonly RTCPeerConnection pc;
        readonly StreamLinkTransport stream;
        readonly RTCDataChannel controlChannel;
        readonly RTCDataChannel[] inputChannels;
        readonly Dictionary<string, VideoStreamTrack> tracks = new Dictionary<string, VideoStreamTrack>();
        bool updaterHeld;
        IDisposable lease;
        RTCPeerConnectionState connectionState = RTCPeerConnectionState.New;
        float disconnectedSince = -1f;
        string endRequested;
        float closeDeadline;

        public string LaptopName { get; }
        public string Address { get; }
        public ControlChannelClient Control { get; }
        public IReadOnlyList<SlotVideoSource> Slots => slots;
        public IReadOnlyList<DataChannelInputSink> Inputs => inputs;
        /// <summary>The WebRTC connection; null over the stream transport.</summary>
        public RTCPeerConnection Peer => pc;
        /// <summary>True over the USB stream transport.</summary>
        public bool IsStream => stream != null;
        /// <summary>The session is over (torn down).</summary>
        public bool IsEnded { get; private set; }
        /// <summary>True from the moment something ended it, until the teardown ran.</summary>
        public bool IsEnding => endRequested != null;
        public string EndReason { get; private set; }
        /// <summary>Ended, but the channels and the connection are still waiting to close (keep ticking).</summary>
        public bool ClosePending { get; private set; }

        public event Action<string> Ended;

        readonly SlotVideoSource[] slots;
        readonly DataChannelInputSink[] inputs;

        /// <param name="pc">A new connection with no transceivers yet; the session creates its data channels.</param>
        internal LinkSession(RTCPeerConnection pc, int slotCount, string laptopName, string address)
        {
            this.pc = pc;
            LaptopName = laptopName;
            Address = address;
            WebRtcUpdater.Acquire();
            updaterHeld = true;

            Control = new ControlChannelClient(SendControl);
            controlChannel = pc.CreateDataChannel("control", new RTCDataChannelInit { negotiated = true, id = 0 });
            controlChannel.OnMessage = bytes => Control.Receive(bytes);
            controlChannel.OnClose = () => RequestEnd("control channel closed");
            slots = new SlotVideoSource[slotCount];
            inputs = new DataChannelInputSink[slotCount];
            inputChannels = new RTCDataChannel[slotCount];
            for (int k = 0; k < slotCount; k++)
            {
                int slot = k;
                var channel = pc.CreateDataChannel($"input-{k}", new RTCDataChannelInit { negotiated = true, id = 1 + k });
                inputChannels[k] = channel;
                slots[k] = new SlotVideoSource(k, new Vector2Int(1920, 1080));
                slots[k].StopRequested += Control.CloseSlot;
                inputs[k] = new DataChannelInputSink(k, Control, json => SendInput(slot, json));
                var sink = inputs[k];
                channel.OnMessage = bytes => sink.ReceiveInput(bytes);
            }

            pc.OnConnectionStateChange = state => connectionState = state;
            pc.OnTrack = e =>
            {
                if (e.Track is VideoStreamTrack video && e.Transceiver != null && e.Transceiver.Mid != null)
                {
                    tracks[e.Transceiver.Mid] = video;
                    AttachTracks();
                }
            };

            Control.Opened += OnOpened;
            Control.WindowEnded += (slot, reason) => EndWindow(slot, reason);
            Control.Ended += RequestEnd;
        }

        /// <summary>Gives each slot its mid (m-line order); a null mid marks a rejected m-line.</summary>
        internal void SetMids(IList<string> mids)
        {
            for (int k = 0; k < slots.Length; k++)
            {
                slots[k].Mid = k < mids.Count ? mids[k] : null;
                slots[k].Usable = slots[k].Mid != null;
            }
            AttachTracks();
        }

        /// <summary>
        /// A session over the stream transport. <paramref name="post"/> runs an action on the main thread (the transport
        /// calls back on its reader thread). The transport is started by <see cref="Begin"/>.
        /// </summary>
        internal LinkSession(StreamLinkTransport transport, Action<Action> post, int slotCount, string laptopName, string address)
        {
            stream = transport;
            LaptopName = laptopName;
            Address = address;
            Control = new ControlChannelClient(SendControl);
            slots = new SlotVideoSource[slotCount];
            inputs = new DataChannelInputSink[slotCount];
            for (int k = 0; k < slotCount; k++)
            {
                int slot = k;
                slots[k] = new SlotVideoSource(k, new Vector2Int(1920, 1080)) { Mid = null, Usable = true };
                slots[k].UseDecoder();
                slots[k].StopRequested += Control.CloseSlot;
                inputs[k] = new DataChannelInputSink(k, Control, json => SendInput(slot, json));
            }
            transport.ControlReceived = bytes => post(() => { if (!IsEnded) Control.Receive(bytes); });
            transport.InputReceived = (slot, bytes) => post(() => { if (!IsEnded) inputs[slot].ReceiveInput(bytes); });
            transport.VideoReceived = (slot, timestamp, key, data) => slots[slot].SubmitVideo(data); // decoded on this thread
            transport.Closed += reason => post(() => RequestEnd("connection lost"));

            Control.Opened += OnOpened;
            Control.WindowEnded += (slot, reason) => EndWindow(slot, reason);
            Control.Ended += RequestEnd;
        }

        void AttachTracks()
        {
            foreach (var slot in slots)
                if (slot.Mid != null && !slot.HasTrack && tracks.TryGetValue(slot.Mid, out var video)) slot.AttachTrack(video);
        }

        /// <summary>The laptop's M5 verified: holds the listener lease while the session lives.</summary>
        internal void Begin(IDisposable listenerLease)
        {
            if (IsEnded || endRequested != null) { listenerLease?.Dispose(); return; }
            lease = listenerLease;
            Control.BeginConnecting();
            stream?.Start(); // the laptop says welcome as soon as it reads M5's success
        }

        void OnOpened(OpenedWindow window)
        {
            if (window.Slot >= slots.Length) return;
            slots[window.Slot].Bind(window.Path, window.Description, window.Width, window.Height, Time.realtimeSinceStartup);
        }

        void EndWindow(int slot, string reason)
        {
            if (slot >= slots.Length) return;
            slots[slot].Unbind(reason == "gone" ? "That window is no longer open on the laptop" : "The laptop stopped streaming that window");
        }

        void SendControl(string json)
        {
            if (stream != null) { if (!IsEnded) stream.Send(StreamLinkTransport.Control, 0, Encoding.UTF8.GetBytes(json)); return; }
            if (!IsEnded && controlChannel.ReadyState == RTCDataChannelState.Open) controlChannel.Send(json);
        }

        void SendInput(int slot, string json)
        {
            if (stream != null) { if (!IsEnded) stream.Send(StreamLinkTransport.Input, slot, Encoding.UTF8.GetBytes(json)); return; }
            if (!IsEnded && inputChannels[slot].ReadyState == RTCDataChannelState.Open) inputChannels[slot].Send(json);
        }

        void RequestEnd(string reason)
        {
            if (endRequested == null && !IsEnded) endRequested = reason;
        }

        /// <summary>Main-thread pump. Call every frame from the moment the session started.</summary>
        public void Tick() => Tick(Time.realtimeSinceStartup);

        public void Tick(float now)
        {
            if (ClosePending)
            {
                bool flushed;
                try { flushed = stream != null ? stream.PendingBytes == 0 : controlChannel.BufferedAmount == 0; }
                catch (Exception) { flushed = true; }
                if (flushed || now >= closeDeadline) CloseConnection();
                return;
            }
            if (IsEnded) return;
            if (endRequested == null && stream == null)
            {
                if (connectionState == RTCPeerConnectionState.Failed || connectionState == RTCPeerConnectionState.Closed)
                    RequestEnd("connection lost");
                else if (connectionState == RTCPeerConnectionState.Disconnected)
                {
                    if (disconnectedSince < 0f) disconnectedSince = now;
                    else if (now - disconnectedSince > DisconnectedLimit) RequestEnd("connection lost");
                }
                else disconnectedSince = -1f;
            }
            if (endRequested != null) { Teardown(endRequested, false); return; }
            Control.Tick(now);
            foreach (var sink in inputs) sink.Tick(now);
            if (stream != null)
                foreach (var slot in slots) slot.Tick(now); // the decoders hand their newest frame to Unity here
        }

        /// <summary>
        /// Panic stop: tells the agent to stop and ends the client and slots at once (no more input). Closing the channels
        /// and the connection waits, best effort, until the stop message left the control channel's buffer or 300 ms
        /// passed (checked in <see cref="Tick"/>), so the agent usually hears it.
        /// </summary>
        public void StopForPanic()
        {
            if (IsEnded) return;
            Control.Stop();
            Teardown("stopped", true);
        }

        public void Dispose()
        {
            if (ClosePending) CloseConnection();
            else Teardown("closed", false);
        }

        void Teardown(string reason, bool flushFirst)
        {
            if (IsEnded) return;
            IsEnded = true;
            EndReason = reason;
            Control.End(reason);
            foreach (var slot in slots)
            {
                slot.Unbind(reason == "stopped" ? "Stopped" : "The laptop connection ended");
                slot.DetachTrack();
            }
            if (pc != null)
            {
                pc.OnTrack = null;
                pc.OnConnectionStateChange = null;
                controlChannel.OnMessage = null;
                controlChannel.OnClose = null;
                foreach (var channel in inputChannels) channel.OnMessage = null;
            }
            else if (stream != null)
            {
                stream.ControlReceived = null;
                stream.InputReceived = null;
            }
            if (flushFirst)
            {
                closeDeadline = Time.realtimeSinceStartup + FlushLimit;
                ClosePending = true;
            }
            else CloseConnection();
            var held = lease;
            lease = null;
            held?.Dispose();
            Ended?.Invoke(reason);
        }

        // Closes the channels and the connection, then disposes the tracks, the channels and the connection, in that order.
        void CloseConnection()
        {
            ClosePending = false;
            if (stream != null)
            {
                stream.VideoReceived = null;
                stream.Close("closed");
                foreach (var slot in slots) slot.DisposeDecoder();
                return;
            }
            try
            {
                controlChannel.Close();
                foreach (var channel in inputChannels) channel.Close();
                pc.Close();
                var disposed = new HashSet<VideoStreamTrack>();
                foreach (var track in tracks.Values)
                    if (track != null && disposed.Add(track)) track.Dispose();
                foreach (var slot in slots)
                {
                    var detached = slot.LastDetached;
                    if (detached != null && disposed.Add(detached)) detached.Dispose();
                }
                controlChannel.Dispose();
                foreach (var channel in inputChannels) channel.Dispose();
                pc.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Link] closing the session: " + e.Message);
            }
            tracks.Clear();
            if (updaterHeld)
            {
                updaterHeld = false;
                WebRtcUpdater.Release();
            }
        }
    }
}
#endif
