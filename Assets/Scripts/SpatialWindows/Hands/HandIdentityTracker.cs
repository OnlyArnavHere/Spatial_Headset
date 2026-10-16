using System.Collections.Generic;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Keeps two hands' identities (left, right) from one hand-pose result to the next. Apple Vision guesses each hand's
    /// side per image and lists hands in no fixed order, and its guess can flip for a frame (a hand seen edge-on, two
    /// hands close together), which would swap the hands' pointers and gestures mid-use. Each result's hands are matched
    /// to the two tracks by how far they moved (in image units); Vision's side only breaks ties, or takes over when it
    /// has disagreed with a track <see cref="SwitchAfter"/> results in a row. A hand with no track nearby starts one: on
    /// Vision's side if it has one, else on the side of the image it is on. Tracks are forgotten <see cref="Memory"/>
    /// seconds after their hand was last seen.
    /// </summary>
    public sealed class HandIdentityTracker
    {
        /// <summary>A hand in one result: its position in the image (0..1) and Vision's side (-1 left, 0 unknown, 1 right).</summary>
        public struct Observation
        {
            public Vector2 Position;
            public int Chirality;

            public Observation(Vector2 position, int chirality)
            {
                Position = position;
                Chirality = chirality;
            }
        }

        /// <summary>Furthest a hand may move between results (image units) and keep its track.</summary>
        public float MaxJump = 0.2f;
        /// <summary>Seconds a track survives without its hand.</summary>
        public float Memory = 0.4f;
        /// <summary>Results in a row Vision must disagree before a track's identity follows Vision's side.</summary>
        public int SwitchAfter = 6;

        const float ChiralityTieBreak = 0.15f, ForcedSwitch = 2f, NewTrack = 1f;

        readonly bool[] active = new bool[2];
        readonly Vector2[] position = new Vector2[2];
        readonly double[] lastSeen = new double[2];
        readonly int[] disagree = new int[2];
        readonly bool[] started = new bool[2];

        public bool IsActive(Handedness hand) => active[(int)hand];
        /// <summary>The track (re)started with the last result: per-hand filters should start fresh.</summary>
        public bool Started(Handedness hand) => started[(int)hand];

        public void Reset()
        {
            for (int k = 0; k < 2; k++) active[k] = started[k] = false;
        }

        static int Side(int track) => track == (int)Handedness.Left ? -1 : 1;

        bool Continues(in Observation o, int track) =>
            active[track] && Vector2.Distance(o.Position, position[track]) <= MaxJump;

        float Cost(in Observation o, int track)
        {
            bool contradicts = o.Chirality != 0 && o.Chirality != Side(track);
            if (Continues(o, track))
            {
                float cost = Vector2.Distance(o.Position, position[track]);
                if (contradicts) cost += disagree[track] + 1 >= SwitchAfter ? ForcedSwitch : ChiralityTieBreak;
                return cost;
            }
            // A new hand: Vision's side if known, else the side of the image it is on.
            if (o.Chirality != 0) return NewTrack + (contradicts ? 0.5f : 0f);
            float towards = track == (int)Handedness.Left ? o.Position.x : 1f - o.Position.x;
            return NewTrack + 0.3f * Mathf.Clamp01(towards);
        }

        /// <summary>
        /// Matches one result's hands to identities: <paramref name="result"/>[i] is the hand
        /// <paramref name="observations"/>[i] belongs to. Only the first two hands are matched (more are left out of the result).
        /// </summary>
        public void Update(IReadOnlyList<Observation> observations, double now, List<Handedness> result)
        {
            result.Clear();
            started[0] = started[1] = false;
            for (int k = 0; k < 2; k++)
                if (active[k] && now - lastSeen[k] > Memory) active[k] = false;

            int count = Mathf.Min(observations.Count, 2);
            if (count == 1)
            {
                var o = observations[0];
                result.Add(Cost(o, 0) <= Cost(o, 1) ? Handedness.Left : Handedness.Right);
            }
            else if (count == 2)
            {
                var a = observations[0];
                var b = observations[1];
                bool straight = Cost(a, 0) + Cost(b, 1) <= Cost(a, 1) + Cost(b, 0);
                result.Add(straight ? Handedness.Left : Handedness.Right);
                result.Add(straight ? Handedness.Right : Handedness.Left);
            }

            for (int i = 0; i < result.Count; i++)
            {
                int k = (int)result[i];
                var o = observations[i];
                started[k] = !Continues(o, k);
                if (started[k]) disagree[k] = 0;
                else disagree[k] = o.Chirality != 0 && o.Chirality != Side(k) ? disagree[k] + 1 : 0;
                active[k] = true;
                position[k] = o.Position;
                lastSeen[k] = now;
            }
        }
    }
}
