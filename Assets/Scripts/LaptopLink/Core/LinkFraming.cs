using System;
using System.Text;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// A refusal in the link handshake or the pairing exchange. <see cref="Reason"/> is short and safe to send to the
    /// peer (the reasons listed in Tools/LaptopAgent/handshake.py). <see cref="Auth"/> marks an authentication failure
    /// (bad signature or code proof); <see cref="Remote"/> marks a refusal the peer told us about, never echoed back.
    /// </summary>
    public sealed class LinkException : Exception
    {
        public string Reason { get; }
        public bool Auth { get; }
        public bool Remote { get; }

        public LinkException(string reason, bool auth = false, bool remote = false) : base(reason)
        {
            Reason = reason;
            Auth = auth;
            Remote = remote;
        }
    }

    /// <summary>
    /// Framing and length-prefix encoding of the laptop link (mirrors handshake.py): a frame is a u32 big-endian
    /// length followed by that many bytes of UTF-8 JSON. A length of 0 or above <see cref="MaxFrame"/> is an error.
    /// <c>enc(x)</c> is u32 BE length || bytes, with strings as UTF-8; the signed transcripts are built from it.
    /// </summary>
    public static class LinkFraming
    {
        public const int MaxFrame = 64 * 1024;

        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        /// <summary>The body length announced by a 4-byte frame header; "bad frame length" when 0 or above the cap.</summary>
        public static int ParseLength(byte[] header)
        {
            if (header == null || header.Length != 4)
                throw new LinkException("bad frame length");
            uint n = ((uint)header[0] << 24) | ((uint)header[1] << 16) | ((uint)header[2] << 8) | header[3];
            if (n == 0 || n > MaxFrame)
                throw new LinkException("bad frame length");
            return (int)n;
        }

        /// <summary>Header plus body for sending. The body must be 1 to <see cref="MaxFrame"/> bytes.</summary>
        public static byte[] Frame(byte[] body)
        {
            if (body == null || body.Length == 0 || body.Length > MaxFrame)
                throw new ArgumentException("A frame body is 1 to " + MaxFrame + " bytes.", nameof(body));
            var frame = new byte[4 + body.Length];
            WriteLength(frame, 0, body.Length);
            Buffer.BlockCopy(body, 0, frame, 4, body.Length);
            return frame;
        }

        /// <summary>enc(x) for bytes.</summary>
        public static byte[] Enc(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var result = new byte[4 + data.Length];
            WriteLength(result, 0, data.Length);
            Buffer.BlockCopy(data, 0, result, 4, data.Length);
            return result;
        }

        /// <summary>enc(x) for a string (UTF-8).</summary>
        public static byte[] Enc(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            return Enc(Utf8.GetBytes(text));
        }

        public static byte[] Concat(params byte[][] parts)
        {
            int total = 0;
            foreach (var p in parts) total += p.Length;
            var result = new byte[total];
            int at = 0;
            foreach (var p in parts)
            {
                Buffer.BlockCopy(p, 0, result, at, p.Length);
                at += p.Length;
            }
            return result;
        }

        static void WriteLength(byte[] target, int offset, int length)
        {
            target[offset] = (byte)(length >> 24);
            target[offset + 1] = (byte)(length >> 16);
            target[offset + 2] = (byte)(length >> 8);
            target[offset + 3] = (byte)length;
        }
    }
}
