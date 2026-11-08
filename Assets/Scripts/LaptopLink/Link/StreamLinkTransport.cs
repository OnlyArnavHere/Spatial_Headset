using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// The session transport for USB without a network: the handshake's own connection (through Apple's USB
    /// multiplexer) carries control, input and video as frames, exactly as Tools/LaptopAgent/stream_link.py:
    /// kind (u8) | slot (u8) | length (u32 big-endian) | payload.
    ///   1 CONTROL both ways, slot 0, one JSON message of at most 16 KiB;
    ///   2 INPUT   both ways, slot k, one JSON message of at most 16 KiB;
    ///   3 VIDEO   laptop to headset, slot k: timestamp (u32 BE, 90 kHz) | flags (u8, bit 0 keyframe) | H.264 Annex B,
    ///             at most 4 MiB.
    /// An unknown kind, a slot at or above <see cref="SlotCount"/>, a length of 0 or over the cap ends the transport.
    /// Plain C#: one reader thread raises the callbacks (marshal to the main thread yourself), one writer thread sends what
    /// <see cref="Send"/> queued, so a slow cable never blocks the caller. <see cref="Closed"/> is raised once, with why.
    /// </summary>
    public sealed class StreamLinkTransport : IDisposable
    {
        public const byte Control = 1, Input = 2, Video = 3;
        public const int MaxMessage = 16 * 1024;
        public const int MaxVideo = 4 * 1024 * 1024;
        public const int HeaderSize = 6;
        /// <summary>More than this queued for sending means the laptop stopped reading: the transport ends.</summary>
        public const int MaxQueuedBytes = 1024 * 1024;

        readonly Stream stream;
        readonly Action closeConnection;
        readonly object gate = new object();
        readonly Queue<byte[]> outgoing = new Queue<byte[]>();
        int queuedBytes;
        bool started, closed;
        Thread reader, writer;

        public int SlotCount { get; }
        public Action<byte[]> ControlReceived;
        public Action<int, byte[]> InputReceived;
        /// <summary>slot, timestamp, keyframe, Annex B data (the frame's payload after its 5-byte video header).</summary>
        public Action<int, uint, bool, ArraySegment<byte>> VideoReceived;
        public event Action<string> Closed;

        public bool IsClosed { get { lock (gate) return closed; } }
        /// <summary>Bytes queued and not yet written (0 means a last message has left).</summary>
        public int PendingBytes { get { lock (gate) return queuedBytes; } }

        public StreamLinkTransport(Stream stream, Action closeConnection, int slotCount)
        {
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            this.closeConnection = closeConnection;
            SlotCount = Math.Max(1, Math.Min(255, slotCount));
        }

        /// <summary>Starts the reader and writer threads (once; set the callbacks first).</summary>
        public void Start()
        {
            lock (gate)
            {
                if (started || closed) return;
                started = true;
            }
            reader = new Thread(ReadLoop) { IsBackground = true, Name = "link-stream-read" };
            writer = new Thread(WriteLoop) { IsBackground = true, Name = "link-stream-write" };
            reader.Start();
            writer.Start();
        }

        /// <summary>Builds a frame; payload limits as for the wire.</summary>
        public static byte[] Frame(byte kind, int slot, byte[] payload)
        {
            var frame = new byte[HeaderSize + payload.Length];
            frame[0] = kind;
            frame[1] = (byte)slot;
            frame[2] = (byte)(payload.Length >> 24);
            frame[3] = (byte)(payload.Length >> 16);
            frame[4] = (byte)(payload.Length >> 8);
            frame[5] = (byte)payload.Length;
            Buffer.BlockCopy(payload, 0, frame, HeaderSize, payload.Length);
            return frame;
        }

        /// <summary>Checks a frame header; returns the payload length or throws InvalidDataException.</summary>
        public static int CheckHeader(byte[] head, int slotCount)
        {
            byte kind = head[0];
            int slot = head[1];
            long length = ((long)head[2] << 24) | ((long)head[3] << 16) | ((long)head[4] << 8) | head[5];
            long cap = kind == Video ? MaxVideo : MaxMessage;
            if ((kind != Control && kind != Input && kind != Video) || length == 0 || length > cap || slot >= Math.Max(1, slotCount))
                throw new InvalidDataException($"bad frame (kind {kind}, slot {slot}, {length} bytes)");
            if (kind == Video && length < 5) throw new InvalidDataException("bad video frame");
            return (int)length;
        }

        /// <summary>Queues a CONTROL or INPUT message. Never blocks; false when closed or the slot is out of range.</summary>
        public bool Send(byte kind, int slot, byte[] payload)
        {
            if (payload == null || payload.Length == 0 || payload.Length > MaxMessage || slot < 0 || slot >= SlotCount) return false;
            var frame = Frame(kind, slot, payload);
            bool overflow;
            lock (gate)
            {
                if (closed) return false;
                overflow = queuedBytes + frame.Length > MaxQueuedBytes;
                if (!overflow)
                {
                    outgoing.Enqueue(frame);
                    queuedBytes += frame.Length;
                    Monitor.PulseAll(gate);
                }
            }
            if (overflow) Close("the laptop stopped reading");
            return !overflow;
        }

        void ReadLoop()
        {
            string reason = "connection lost";
            var head = new byte[HeaderSize];
            try
            {
                while (true)
                {
                    if (!ReadExact(head, HeaderSize)) { reason = "the laptop closed the connection"; break; }
                    int length = CheckHeader(head, SlotCount);
                    var payload = new byte[length];
                    if (!ReadExact(payload, length)) { reason = "the laptop closed the connection"; break; }
                    int slot = head[1];
                    switch (head[0])
                    {
                        case Control: ControlReceived?.Invoke(payload); break;
                        case Input: InputReceived?.Invoke(slot, payload); break;
                        default:
                            uint timestamp = ((uint)payload[0] << 24) | ((uint)payload[1] << 16) | ((uint)payload[2] << 8) | payload[3];
                            bool key = (payload[4] & 1) != 0;
                            VideoReceived?.Invoke(slot, timestamp, key, new ArraySegment<byte>(payload, 5, payload.Length - 5));
                            break;
                    }
                }
            }
            catch (InvalidDataException e) { reason = e.Message; }
            catch (Exception) when (IsClosed) { reason = null; }
            catch (Exception e) { reason = "connection lost (" + e.GetType().Name + ")"; }
            Close(reason);
        }

        bool ReadExact(byte[] buffer, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = stream.Read(buffer, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        void WriteLoop()
        {
            try
            {
                while (true)
                {
                    byte[] frame;
                    lock (gate)
                    {
                        while (outgoing.Count == 0 && !closed) Monitor.Wait(gate);
                        if (outgoing.Count == 0) return;
                        frame = outgoing.Peek();
                    }
                    stream.Write(frame, 0, frame.Length);
                    stream.Flush();
                    lock (gate)
                    {
                        if (outgoing.Count > 0) outgoing.Dequeue();
                        queuedBytes -= frame.Length;
                        Monitor.PulseAll(gate);
                    }
                }
            }
            catch (Exception) when (IsClosed) { }
            catch (Exception e) { Close("connection lost (" + e.GetType().Name + ")"); }
        }

        /// <summary>Waits up to <paramref name="timeout"/> for the queue to drain, so a last "stop" leaves before closing.</summary>
        public void Flush(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            lock (gate)
            {
                while (queuedBytes > 0 && !closed)
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) return;
                    Monitor.Wait(gate, left);
                }
            }
        }

        /// <summary>Closes the connection (both threads end); raises <see cref="Closed"/> once. Safe from any thread.</summary>
        public void Close(string reason = "closed")
        {
            lock (gate)
            {
                if (closed) return;
                closed = true;
                outgoing.Clear();
                queuedBytes = 0;
                Monitor.PulseAll(gate);
            }
            try { stream.Dispose(); } catch (Exception) { }
            try { closeConnection?.Invoke(); } catch (Exception) { }
            if (reason != null) Closed?.Invoke(reason);
            else Closed?.Invoke("closed");
        }

        public void Dispose() => Close();
    }
}
