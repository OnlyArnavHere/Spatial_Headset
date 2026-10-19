using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>One link connection as JSON frames (u32 length + a strict JSON object). Not thread safe: one exchange uses it.</summary>
    public interface IFrameChannel
    {
        /// <summary>
        /// Reads one frame. <paramref name="timeout"/> is per read (Timeout.InfiniteTimeSpan for none): "read timeout".
        /// Raises LinkException for a bad frame, a wrong version, a peer error frame (Remote) or a closed connection,
        /// OperationCanceledException when <paramref name="ct"/> is cancelled.
        /// </summary>
        Task<Dictionary<string, object>> ReadFrameAsync(TimeSpan timeout, CancellationToken ct);
        Task WriteFrameAsync(Dictionary<string, object> frame, CancellationToken ct = default(CancellationToken));
        /// <summary>Best effort {"v":..,"type":"error","reason":..} within one second; never throws.</summary>
        Task SendErrorAsync(string reason);
        /// <summary>Closes the connection; safe to call twice.</summary>
        void Close();
    }

    /// <summary>
    /// <see cref="IFrameChannel"/> over a stream (a NetworkStream). Frames are at most 64 KiB; <see cref="Version"/> is the
    /// "v" every frame must carry (0 until the first frame has been routed); at most <see cref="MaxFrames"/> frames are
    /// read or written ("too many frames"). A read that is cancelled or times out leaves the stream open so an error
    /// frame can still be sent; <see cref="Close"/> ends it.
    /// </summary>
    public sealed class NetworkFrameChannel : IFrameChannel
    {
        readonly Stream stream;
        readonly Action close;
        int frames;
        bool closed;

        public int Version { get; set; }
        public int MaxFrames { get; set; }

        public NetworkFrameChannel(Stream stream, Action close = null, int version = 0, int maxFrames = 8)
        {
            this.stream = stream;
            this.close = close;
            Version = version;
            MaxFrames = maxFrames;
        }

        /// <summary>The checks every received frame gets: a string "type", the exact integer "v", and peer error frames.</summary>
        public static void Validate(Dictionary<string, object> frame, int version)
        {
            if (!(frame.TryGetValue("type", out var type) && type is string typeText))
                throw new LinkException("malformed frame");
            if (!LinkJson.IsInteger(frame.TryGetValue("v", out var v) ? v : null, version))
                throw new LinkException("unsupported version");
            if (typeText == "error")
            {
                string reason = LinkJson.GetString(frame, "reason");
                if (reason == null || !IsPrintable(reason)) reason = "?";
                if (reason.Length > 100) reason = reason.Substring(0, 100);
                throw new LinkException("peer refused: " + reason, remote: true);
            }
        }

        static bool IsPrintable(string s)
        {
            foreach (char c in s)
                if (char.IsControl(c) || (int)c == 0x2028 || (int)c == 0x2029) return false;
            return true;
        }

        void Count()
        {
            if (++frames > MaxFrames) throw new LinkException("too many frames");
        }

        public async Task<Dictionary<string, object>> ReadFrameAsync(TimeSpan timeout, CancellationToken ct)
        {
            Count();
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (timeout != Timeout.InfiniteTimeSpan) cts.CancelAfter(timeout);
                Dictionary<string, object> frame;
                try
                {
                    var head = new byte[4];
                    await ReadExact(head, 4, cts.Token).ConfigureAwait(false);
                    int length = LinkFraming.ParseLength(head);
                    var body = new byte[length];
                    await ReadExact(body, length, cts.Token).ConfigureAwait(false);
                    frame = LinkJson.ParseObject(body);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new LinkException("read timeout");
                }
                if (Version != 0) Validate(frame, Version);
                else if (!(frame.TryGetValue("type", out var type) && type is string))
                    throw new LinkException("malformed frame");
                return frame;
            }
        }

        public async Task WriteFrameAsync(Dictionary<string, object> frame, CancellationToken ct = default(CancellationToken))
        {
            byte[] body = LinkJson.ToBytes(frame);
            if (body.Length > LinkFraming.MaxFrame) throw new LinkException("frame too large");
            Count();
            await WriteBytes(LinkFraming.Frame(body), ct).ConfigureAwait(false);
        }

        public async Task SendErrorAsync(string reason)
        {
            try
            {
                var frame = new Dictionary<string, object>
                {
                    ["v"] = (long)(Version == 0 ? 1 : Version),
                    ["type"] = "error",
                    ["reason"] = reason,
                };
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                    await WriteBytes(LinkFraming.Frame(LinkJson.ToBytes(frame)), cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            try { stream.Dispose(); } catch (Exception) { }
            try { close?.Invoke(); } catch (Exception) { }
        }

        async Task ReadExact(byte[] buffer, int count, CancellationToken token)
        {
            int got = 0;
            while (got < count)
            {
                token.ThrowIfCancellationRequested();
                int n;
                try
                {
                    n = await WithCancel(stream.ReadAsync(buffer, got, count - got), token).ConfigureAwait(false);
                }
                catch (Exception e) when (IsConnectionError(e))
                {
                    throw new LinkException("connection closed");
                }
                if (n <= 0) throw new LinkException("connection closed");
                got += n;
            }
        }

        async Task WriteBytes(byte[] data, CancellationToken token)
        {
            try
            {
                await WithCancel(WriteThenZero(data), token).ConfigureAwait(false);
            }
            catch (Exception e) when (IsConnectionError(e))
            {
                throw new LinkException("connection closed");
            }
        }

        async Task<int> WriteThenZero(byte[] data)
        {
            await stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            return 0;
        }

        static bool IsConnectionError(Exception e)
        {
            return e is IOException || e is SocketException || e is ObjectDisposedException || e is InvalidOperationException;
        }

        // Stream cancellation support differs between runtimes, so race the I/O against the token ourselves.
        static async Task<T> WithCancel<T>(Task<T> task, CancellationToken token)
        {
            if (!task.IsCompleted)
            {
                var cancelled = new TaskCompletionSource<bool>();
                using (token.Register(() => cancelled.TrySetResult(true)))
                {
                    if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
                    {
                        _ = task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        throw new OperationCanceledException(token);
                    }
                }
            }
            return await task.ConfigureAwait(false);
        }
    }

    /// <summary>Hands out an already read first frame once, then reads from the wrapped channel.</summary>
    public sealed class ReplayChannel : IFrameChannel
    {
        readonly IFrameChannel inner;
        Dictionary<string, object> first;

        public ReplayChannel(IFrameChannel inner, Dictionary<string, object> first)
        {
            this.inner = inner;
            this.first = first;
        }

        public Task<Dictionary<string, object>> ReadFrameAsync(TimeSpan timeout, CancellationToken ct)
        {
            if (first == null) return inner.ReadFrameAsync(timeout, ct);
            var frame = first;
            first = null;
            return Task.FromResult(frame);
        }

        public Task WriteFrameAsync(Dictionary<string, object> frame, CancellationToken ct = default(CancellationToken)) { return inner.WriteFrameAsync(frame, ct); }
        public Task SendErrorAsync(string reason) { return inner.SendErrorAsync(reason); }
        public void Close() { inner.Close(); }
    }

    /// <summary>Building and checking the fields of link messages (the helpers of handshake.py).</summary>
    public static class LinkMessages
    {
        public const int MaxSdpBytes = 48 * 1024;
        public const int MaxSpki = 1024;
        public const int NonceLength = 32;

        static readonly System.Text.UTF8Encoding StrictUtf8 = new System.Text.UTF8Encoding(false, true);

        /// <summary>{"v":version,"type":type, key: value, ...} from key/value pairs.</summary>
        public static Dictionary<string, object> Frame(int version, string type, params object[] keyValues)
        {
            var frame = new Dictionary<string, object> { ["v"] = (long)version, ["type"] = type };
            for (int i = 0; i + 1 < keyValues.Length; i += 2) frame[(string)keyValues[i]] = keyValues[i + 1];
            return frame;
        }

        /// <summary>The frame itself when its type is <paramref name="type"/>; otherwise "unexpected message".</summary>
        public static Dictionary<string, object> Require(Dictionary<string, object> frame, string type)
        {
            if (LinkJson.GetString(frame, "type") != type) throw new LinkException("unexpected message");
            return frame;
        }

        public static string B64(byte[] data) { return StrictBase64.Encode(data); }

        /// <summary>A base64 field of exactly <paramref name="exact"/> bytes, or 1..<paramref name="max"/> bytes.</summary>
        public static byte[] Bytes(Dictionary<string, object> frame, string key, int exact = -1, int max = -1)
        {
            if (!(frame.TryGetValue(key, out var value) && value is string text)) throw new LinkException("missing " + key);
            if (!StrictBase64.TryDecode(text, out var data)) throw new LinkException("malformed " + key);
            if ((exact >= 0 && data.Length != exact) || (max >= 0 && (data.Length == 0 || data.Length > max)))
                throw new LinkException("malformed " + key);
            return data;
        }

        public static byte[] PublicKey(Dictionary<string, object> frame, string key = "spki")
        {
            byte[] spki = Bytes(frame, key, max: MaxSpki);
            if (!Spki.TryParse(spki, out _)) throw new LinkException("bad public key");
            return spki;
        }

        public static string Sdp(Dictionary<string, object> frame)
        {
            if (!(frame.TryGetValue("sdp", out var value) && value is string sdp)) throw new LinkException("missing sdp");
            return CheckSdp(sdp);
        }

        public static string CheckSdp(string sdp)
        {
            int size;
            try { size = StrictUtf8.GetByteCount(sdp); }
            catch (System.Text.EncoderFallbackException) { throw new LinkException("malformed sdp"); }
            if (size == 0 || size > MaxSdpBytes) throw new LinkException("sdp too large");
            return sdp;
        }
    }
}
