using System;
using System.Diagnostics;

namespace SpatialLauncher.LaptopLink
{
    public enum PairCodeEnd { Expired, Used, StruckOut, Replaced }

    public enum PairProofOutcome
    {
        /// <summary>No such active code (none, expired, replaced or already spent): "not pairing".</summary>
        NotPairing,
        /// <summary>The proof was right; the code is now spent.</summary>
        Accepted,
        /// <summary>The proof was wrong; a strike was counted.</summary>
        WrongCode,
        /// <summary>The proof was wrong and it was the last strike; the code is dead.</summary>
        WrongCodeStruckOut,
    }

    /// <summary>A code handed out by <see cref="PairCodeAuthority.Issue"/>.</summary>
    public sealed class IssuedPairCode
    {
        public long Id { get; }
        /// <summary>Canonical 8 characters, or 20 from a scanned QR (feeds the key derivation).</summary>
        public string Code { get; }
        public string Formatted { get { return PairCode.Format(Code); } }
        /// <summary>For a code read from a laptop's QR: that laptop's fingerprint (only it may pair). Null for a shown code.</summary>
        public string QrFingerprint { get; }
        /// <summary>The QR's id (<see cref="PairCode.QrId"/>), or null for a shown code.</summary>
        public string QrId { get; }
        public bool FromQr { get { return QrFingerprint != null; } }

        public IssuedPairCode(long id, string code, string qrFingerprint = null)
        {
            Id = id;
            Code = code;
            QrFingerprint = qrFingerprint;
            QrId = qrFingerprint != null ? PairCode.QrId(code) : null;
        }
    }

    /// <summary>
    /// The headset's pairing-code policy (a shown code, or one read from a laptop's QR): one active code that lasts 120 s (Stopwatch), works once (the first correct
    /// proof spends it, whatever happens afterwards) and dies after 3 wrong proofs in total from any address. Issuing a
    /// new code kills the old one. <see cref="TryApplyProof"/> checks "this code is still active" and applies the
    /// strike or consume as one step under a lock, so it is safe from the worker thread that ran the key derivation.
    /// <see cref="CodeEnded"/> is raised outside the lock, from whichever thread noticed.
    /// </summary>
    public sealed class PairCodeAuthority
    {
        public const double Ttl = 120.0;
        public const int MaxWrong = 3;

        readonly object gate = new object();
        readonly Func<double> clock;
        readonly double ttl;
        IssuedPairCode active;
        double expires;
        int wrong;
        long nextId;

        /// <summary>Raised with the id of the code and why it ended.</summary>
        public event Action<long, PairCodeEnd> CodeEnded;

        public PairCodeAuthority() : this(null, Ttl) { }

        public PairCodeAuthority(Func<double> secondsClock, double ttlSeconds)
        {
            clock = secondsClock ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
            ttl = ttlSeconds;
        }

        /// <summary>A fresh code; any old one is killed.</summary>
        public IssuedPairCode Issue()
        {
            return Install(PairCode.New(), null);
        }

        /// <summary>
        /// The code from a laptop's QR (<see cref="PairCode.TryParseQr"/>) replaces any code, with the same lifetime and
        /// strike rules; only the laptop with <paramref name="fingerprint"/> may use it.
        /// </summary>
        public IssuedPairCode ArmQr(string code, string fingerprint)
        {
            if (code == null || fingerprint == null) throw new ArgumentNullException(code == null ? "code" : "fingerprint");
            return Install(code, fingerprint);
        }

        IssuedPairCode Install(string code, string qrFingerprint)
        {
            long replacedId = 0;
            bool replaced = false;
            IssuedPairCode issued;
            lock (gate)
            {
                if (active != null)
                {
                    replaced = true;
                    replacedId = active.Id;
                }
                issued = new IssuedPairCode(++nextId, code, qrFingerprint);
                active = issued;
                expires = clock() + ttl;
                wrong = 0;
            }
            if (replaced) CodeEnded?.Invoke(replacedId, PairCodeEnd.Replaced);
            return issued;
        }

        /// <summary>The active code, or null. Notices expiry.</summary>
        public IssuedPairCode Active
        {
            get
            {
                IssuedPairCode result;
                long expiredId = 0;
                bool expired = false;
                lock (gate)
                {
                    if (active != null && clock() >= expires)
                    {
                        expired = true;
                        expiredId = active.Id;
                        active = null;
                    }
                    result = active;
                }
                if (expired) CodeEnded?.Invoke(expiredId, PairCodeEnd.Expired);
                return result;
            }
        }

        public double RemainingSeconds
        {
            get
            {
                lock (gate) return active == null ? 0 : Math.Max(0, expires - clock());
            }
        }

        public int Strikes
        {
            get
            {
                lock (gate) return wrong;
            }
        }

        /// <summary>Kills the active code, if any (for example when the user leaves the pairing screen).</summary>
        public void Cancel()
        {
            long id = 0;
            bool had = false;
            lock (gate)
            {
                if (active != null)
                {
                    had = true;
                    id = active.Id;
                    active = null;
                }
            }
            if (had) CodeEnded?.Invoke(id, PairCodeEnd.Replaced);
        }

        /// <summary>
        /// Applies the result of a MAC check against code <paramref name="codeId"/>. A right proof consumes the code, a
        /// wrong one counts a strike and the third kills it. Atomic.
        /// </summary>
        public PairProofOutcome TryApplyProof(long codeId, bool macOk)
        {
            PairProofOutcome outcome;
            PairCodeEnd? ended = null;
            lock (gate)
            {
                if (active == null || active.Id != codeId)
                {
                    return PairProofOutcome.NotPairing;
                }
                if (clock() >= expires)
                {
                    active = null;
                    outcome = PairProofOutcome.NotPairing;
                    ended = PairCodeEnd.Expired;
                }
                else if (macOk)
                {
                    active = null;
                    outcome = PairProofOutcome.Accepted;
                    ended = PairCodeEnd.Used;
                }
                else if (++wrong >= MaxWrong)
                {
                    active = null;
                    outcome = PairProofOutcome.WrongCodeStruckOut;
                    ended = PairCodeEnd.StruckOut;
                }
                else
                {
                    outcome = PairProofOutcome.WrongCode;
                }
            }
            if (ended.HasValue) CodeEnded?.Invoke(codeId, ended.Value);
            return outcome;
        }
    }
}
