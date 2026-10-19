using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// The hand thresholds <see cref="HoloKitHandSource"/> and the gesture recognizers use: pinch on/off (thumb to index
    /// as a fraction of the hand's size), the finger ratios behind fist and openness, and the hand length behind
    /// hand-size depth. <see cref="Default"/> is what they were before Hand Setup existed.
    /// </summary>
    [Serializable]
    public struct HandThresholds
    {
        public float PinchOn, PinchOff;
        /// <summary>A finger counts as curled at or below this tip/knuckle ratio (openness 0 there).</summary>
        public float CurledRatio;
        /// <summary>Openness reaches 1 at this ratio.</summary>
        public float ExtendedRatio;
        /// <summary>Wrist to knuckles, metres (hand-size depth).</summary>
        public float HandLength;

        public static HandThresholds Default => new HandThresholds
        {
            PinchOn = 0.25f, PinchOff = 0.40f, CurledRatio = 1.15f, ExtendedRatio = 1.7f, HandLength = 0.09f,
        };
    }

    /// <summary>
    /// What Hand Setup measured and saved (Settings > Hands > Hand setup), part of <see cref="HandSettings"/>. Until a
    /// calibration is saved it holds the defaults. Reset goes back to them.
    /// </summary>
    [Serializable]
    public class HandCalibration
    {
        public bool calibrated;
        public float pinchOn = 0.25f;
        public float pinchOff = 0.40f;
        public float curledRatio = 1.15f;
        public float extendedRatio = 1.7f;
        /// <summary>Wrist to knuckles, metres: measured with LiDAR, or typed in (6..13 cm).</summary>
        public float handLength = 0.09f;
        /// <summary>The hand length came from LiDAR depth (not the default or a typed value).</summary>
        public bool handLengthMeasured;
        /// <summary>When it was saved (local time, "yyyy-MM-dd HH:mm"), or empty.</summary>
        public string savedAt = "";
        // The medians it was derived from, shown in Hand Setup's result.
        public float openPinchRatio, pinchPinchRatio, openExtension, fistExtension;

        public const float MinHandLength = 0.06f, MaxHandLength = 0.13f;

        public HandThresholds Thresholds => new HandThresholds
        {
            PinchOn = pinchOn, PinchOff = pinchOff, CurledRatio = curledRatio, ExtendedRatio = extendedRatio, HandLength = handLength,
        };

        public HandCalibration Clone() => (HandCalibration)MemberwiseClone();

        /// <summary>Puts every value back in range (a hand-edited or damaged settings file); defaults where it makes no sense.</summary>
        public void Sanitize()
        {
            bool Bad(float v) => float.IsNaN(v) || float.IsInfinity(v);
            var d = HandThresholds.Default;
            if (Bad(pinchOn) || Bad(pinchOff) || Bad(curledRatio) || Bad(extendedRatio)) { pinchOn = d.PinchOn; pinchOff = d.PinchOff; curledRatio = d.CurledRatio; extendedRatio = d.ExtendedRatio; calibrated = false; }
            pinchOn = Mathf.Clamp(pinchOn, 0.05f, 0.6f);
            pinchOff = Mathf.Clamp(pinchOff, pinchOn + 0.05f, 0.9f);
            curledRatio = Mathf.Clamp(curledRatio, 0.8f, 1.6f);
            extendedRatio = Mathf.Clamp(extendedRatio, curledRatio + 0.2f, 2.6f);
            handLength = Bad(handLength) ? d.HandLength : Mathf.Clamp(handLength, MinHandLength, MaxHandLength);
            savedAt ??= "";
        }

        /// <summary>One line for Settings: "Calibrated 2026-10-17 14:05 (pinch 0.21/0.36, hand 8.6 cm measured)" or the defaults.</summary>
        public string Summary => calibrated
            ? $"Calibrated {savedAt}: pinch {pinchOn:0.00}/{pinchOff:0.00}, curl {curledRatio:0.00}, hand {handLength * 100f:0.0} cm" +
              (handLengthMeasured ? " (measured)" : "")
            : $"Defaults (not calibrated), hand {handLength * 100f:0.0} cm{(handLengthMeasured ? " (measured)" : "")}";
    }

    /// <summary>
    /// Hand Setup's measurement: samples of one hand held open, pinched and in a fist (from <see cref="HandState"/>'s raw
    /// measures), then thresholds set between the user's own poses. Pinch turns on 30% of the way from their pinch to
    /// their open hand and off at 50% (hysteresis). A finger counts as curled 25% of the way from their fist to their
    /// open hand, and openness is 1 at 90% of the way, so the gesture recognizers' fist and open-palm thresholds (0.25,
    /// 0.65 openness) fall at the same places in the user's own range. The hand length is the median LiDAR measurement
    /// when most samples had metric depth; otherwise the previous (default or typed) value is kept.
    /// </summary>
    public class HandCalibrator
    {
        public enum Pose { Open, Pinch, Fist }

        /// <summary>Samples each pose needs (about a second at 30 Hz tracking).</summary>
        public const int SamplesNeeded = 30;

        readonly List<float>[] pinchRatios = { new List<float>(), new List<float>(), new List<float>() };
        readonly List<float>[] extensions = { new List<float>(), new List<float>(), new List<float>() };
        readonly List<float> handSizes = new List<float>();
        int samples, metricSamples;

        public int Count(Pose pose) => pinchRatios[(int)pose].Count;
        public float Progress(Pose pose) => Mathf.Clamp01(Count(pose) / (float)SamplesNeeded);
        public bool Done(Pose pose) => Count(pose) >= SamplesNeeded;

        public void Clear()
        {
            for (int i = 0; i < 3; i++)
            {
                pinchRatios[i].Clear();
                extensions[i].Clear();
            }
            handSizes.Clear();
            samples = metricSamples = 0;
        }

        public void Clear(Pose pose)
        {
            pinchRatios[(int)pose].Clear();
            extensions[(int)pose].Clear();
        }

        /// <summary>Adds one frame of a tracked hand held in <paramref name="pose"/>; false (ignored) if it has no raw measures.</summary>
        public bool Add(Pose pose, in HandState hand)
        {
            if (!hand.IsTracked || hand.PinchRatio <= 0f || hand.FingerExtension <= 0f || Done(pose)) return false;
            pinchRatios[(int)pose].Add(hand.PinchRatio);
            extensions[(int)pose].Add(hand.FingerExtension);
            samples++;
            if (hand.MetricDepth && hand.HandSize > 0f)
            {
                metricSamples++;
                handSizes.Add(hand.HandSize);
            }
            return true;
        }

        public static float Median(List<float> values)
        {
            if (values.Count == 0) return 0f;
            var sorted = new List<float>(values);
            sorted.Sort();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) * 0.5f;
        }

        /// <summary>
        /// The calibration from the samples, on top of <paramref name="previous"/> (its hand length is kept unless LiDAR
        /// measured one). False with a reason when a pose is missing or the poses weren't different enough.
        /// </summary>
        public bool TryCompute(HandCalibration previous, out HandCalibration result, out string problem)
        {
            result = (previous ?? new HandCalibration()).Clone();
            problem = "";
            foreach (Pose pose in Enum.GetValues(typeof(Pose)))
                if (!Done(pose))
                {
                    problem = $"Not enough samples of {pose.ToString().ToLowerInvariant()}";
                    return false;
                }

            float openPinch = Median(pinchRatios[(int)Pose.Open]), pinchPinch = Median(pinchRatios[(int)Pose.Pinch]);
            float openExt = Median(extensions[(int)Pose.Open]), fistExt = Median(extensions[(int)Pose.Fist]);
            if (openPinch - pinchPinch < 0.15f || pinchPinch > openPinch * 0.6f)
            {
                problem = $"The pinch didn't close enough (thumb to index {pinchPinch:0.00} pinched, {openPinch:0.00} open). Touch the tips together.";
                return false;
            }
            if (openExt - fistExt < 0.25f)
            {
                problem = $"The fist and open hand looked too alike (finger ratio {fistExt:0.00} vs {openExt:0.00}). Curl the fingers fully, then spread them.";
                return false;
            }

            result.openPinchRatio = openPinch;
            result.pinchPinchRatio = pinchPinch;
            result.openExtension = openExt;
            result.fistExtension = fistExt;
            result.pinchOn = pinchPinch + 0.3f * (openPinch - pinchPinch);
            result.pinchOff = pinchPinch + 0.5f * (openPinch - pinchPinch);
            result.curledRatio = fistExt + 0.25f * (openExt - fistExt);
            result.extendedRatio = fistExt + 0.9f * (openExt - fistExt);
            if (samples > 0 && metricSamples * 2 >= samples)
            {
                float length = Median(handSizes);
                if (length >= HandCalibration.MinHandLength && length <= HandCalibration.MaxHandLength)
                {
                    result.handLength = length;
                    result.handLengthMeasured = true;
                }
            }
            result.calibrated = true;
            result.savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            result.Sanitize();
            return true;
        }
    }
}
