using System;
using System.Collections.Generic;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Hand Setup (Settings > Hands > Calibrate): a guided window that measures the user's own open palm, pinch and fist
    /// and sets the hand thresholds between them (<see cref="HandCalibrator"/>), saved per user in
    /// <see cref="HandSettings.calibration"/>. Each pose has a short get-ready countdown, then about a second of samples
    /// while a progress ring fills; a lost hand pauses the ring, and a pose not finished within
    /// <see cref="PoseTimeout"/> ends the run. The result shows the measured and derived values with Save and Retry. While
    /// it measures, the hand pointers and gestures are paused, so pinching and making a fist can't press buttons or move
    /// windows. On phones without LiDAR the hand length can't be measured: the intro has a slider to type it in (wrist
    /// crease to the middle finger's knuckle). Reset puts the defaults back.
    /// </summary>
    public class HandSetupContent : WindowContent
    {
        public enum Step { Intro, GetReady, Measure, Result }

        static readonly Color PanelColor = new Color(0.13f, 0.14f, 0.17f);
        static readonly Color ButtonColor = new Color(0.32f, 0.34f, 0.40f);
        static readonly Color PrimaryColor = new Color(0.16f, 0.36f, 0.95f);
        static readonly Color RingOff = new Color(0.25f, 0.26f, 0.31f);
        static readonly Color RingOn = new Color(0.20f, 0.78f, 0.55f);
        static readonly Color RingReady = new Color(0.95f, 0.75f, 0.25f);
        static readonly Color TrackColor = new Color(0.24f, 0.25f, 0.30f);

        public const float GetReadyTime = 1.5f;
        public const float PoseTimeout = 15f;
        const int RingSegments = 24;
        const float ButtonHeight = 0.04f, ButtonWidth = 0.12f, Gap = 0.012f;

        public static readonly Vector2 PreferredSize = new Vector2(0.62f, 0.4f);

        static readonly HandCalibrator.Pose[] Poses = { HandCalibrator.Pose.Open, HandCalibrator.Pose.Pinch, HandCalibrator.Pose.Fist };

        public static string Instruction(HandCalibrator.Pose pose) => pose switch
        {
            HandCalibrator.Pose.Open => "Open palm: fingers straight and spread, palm towards you, about 40 cm in front.",
            HandCalibrator.Pose.Pinch => "Pinch: touch the tips of your thumb and index finger, other fingers relaxed.",
            _ => "Fist: curl all your fingers in tightly.",
        };

        /// <summary>Where hands come from (the launcher's hand input by default; tests set their own).</summary>
        public IHandSource Source { get; set; }
        public Step CurrentStep { get; private set; }
        public HandCalibrator.Pose CurrentPose => Poses[poseIndex];
        public HandCalibrator Calibrator { get; } = new HandCalibrator();
        /// <summary>The calibration measured in the last run (shown in Result), or null.</summary>
        public HandCalibration Result { get; private set; }
        /// <summary>Why the last run failed, or "".</summary>
        public string Problem { get; private set; } = "";
        public string Heading => heading != null ? heading.text : "";
        public string Body => body != null ? body.text : "";
        /// <summary>The ring's fill, 0..1 (the countdown while getting ready, the samples while measuring).</summary>
        public float RingFill { get; private set; }
        /// <summary>The hand being measured.</summary>
        public Handedness MeasuredHand { get; private set; }

        public WindowButton PrimaryButton { get; private set; }
        public WindowButton SecondaryButton { get; private set; }
        public WindowButton TertiaryButton { get; private set; }
        public WindowSlider HandLengthSlider { get; private set; }
        public string PrimaryLabel => labels[0].text;
        public string SecondaryLabel => labels[1].text;
        public string TertiaryLabel => labels[2].text;

        HandWindowInput hands;
        GestureRecognizers gestures;
        TextMeshPro heading, body, status, lengthLabel;
        Transform panel, ring, lengthFill;
        readonly List<Material> ringMaterials = new List<Material>();
        readonly List<Transform> ringSegments = new List<Transform>();
        readonly TextMeshPro[] labels = new TextMeshPro[3];
        readonly WindowButton[] buttons = new WindowButton[3];
        readonly Material[] buttonMaterials = new Material[3];
        readonly Action[] actions = new Action[3];
        int poseIndex;
        float stepTime, poseTime;
        bool paused;

        public void Init(SpatialWindow window, Material baseMaterial, HandWindowInput handInput, GestureRecognizers gestureRecognizers)
        {
            Attach(window, baseMaterial);
            hands = handInput;
            gestures = gestureRecognizers;
            Source = handInput != null ? handInput.HandSource : null;

            panel = CreateQuad("Panel", transform, CreateMaterial(PanelColor));
            heading = CreateLabel("Heading", transform, "", TextRole.Title, TextAlignmentOptions.MidlineLeft);
            body = CreateLabel("Body", transform, "", TextRole.Body, TextAlignmentOptions.TopLeft);
            body.textWrappingMode = TextWrappingModes.Normal;
            body.overflowMode = TextOverflowModes.Truncate;
            status = CreateLabel("Status", transform, "", TextRole.Secondary, TextAlignmentOptions.MidlineLeft);

            ring = new GameObject("Ring").transform;
            ring.SetParent(transform, false);
            for (int i = 0; i < RingSegments; i++)
            {
                var material = CreateMaterial(RingOff);
                var segment = CreateQuad($"Segment {i}", ring, material);
                ringMaterials.Add(material);
                ringSegments.Add(segment);
            }

            for (int i = 0; i < 3; i++)
            {
                int index = i;
                buttonMaterials[i] = CreateMaterial(i == 0 ? PrimaryColor : ButtonColor);
                buttons[i] = CreateControl<WindowButton>($"Button {i}", transform, buttonMaterials[i]);
                labels[i] = CreateLabel($"ButtonLabel {i}", transform, "", TextRole.Headline, TextAlignmentOptions.Center);
                buttons[i].Clicked += () => actions[index]?.Invoke();
            }
            PrimaryButton = buttons[0];
            SecondaryButton = buttons[1];
            TertiaryButton = buttons[2];

            HandLengthSlider = CreateControl<WindowSlider>("HandLength", transform, CreateMaterial(TrackColor));
            lengthFill = CreateQuad("HandLengthFill", transform, CreateMaterial(PrimaryColor));
            lengthLabel = CreateLabel("HandLengthLabel", transform, "", TextRole.Body, TextAlignmentOptions.MidlineLeft);
            HandLengthSlider.ValueChanged += t => SetHandLength(Mathf.Lerp(HandCalibration.MinHandLength, HandCalibration.MaxHandLength, t));

            ShowIntro("");
            Relayout();
        }

        /// <summary>Types in the hand length (metres, rounded to 0.5 cm), as the intro's slider does.</summary>
        public void SetHandLength(float metres)
        {
            float rounded = Mathf.Round(Mathf.Clamp(metres, HandCalibration.MinHandLength, HandCalibration.MaxHandLength) * 200f) / 200f;
            SettingsStore.Update(s =>
            {
                s.hands.calibration.handLength = rounded;
                s.hands.calibration.handLengthMeasured = false;
            });
            RefreshLength();
        }

        /// <summary>Starts measuring (Start / Retry).</summary>
        public void Begin()
        {
            Calibrator.Clear();
            Result = null;
            Problem = "";
            poseIndex = 0;
            EnterGetReady();
        }

        /// <summary>Stops measuring and goes back to the intro.</summary>
        public void Cancel() => ShowIntro("Cancelled. Nothing was changed.");

        /// <summary>Saves the last result (Save).</summary>
        public void Save()
        {
            if (Result == null) return;
            var saved = Result.Clone();
            SettingsStore.Update(s => s.hands.calibration = saved);
            ShowIntro("Saved. Pinch, fist and openness now use your own hand.");
        }

        /// <summary>Puts the default thresholds back (Reset).</summary>
        public void ResetToDefaults()
        {
            SettingsStore.Update(s => s.hands.calibration = new HandCalibration());
            ShowIntro("Reset to the defaults.");
        }

        void ShowIntro(string note)
        {
            SetPaused(false);
            CurrentStep = Step.Intro;
            RingFill = 0f;
            heading.text = "Hand Setup";
            body.text = "Measures your open palm, pinch and fist (a second each) and sets the thresholds to your hand. " +
                        "Hold one hand up in front of you; the other can rest.\n" + (note.Length > 0 ? note : SettingsStore.Current.hands.calibration.Summary);
            SetButtons(("Start", Begin), ("Reset", ResetToDefaults), (null, null));
            SetLengthVisible(true);
        }

        void EnterGetReady()
        {
            CurrentStep = Step.GetReady;
            stepTime = 0f;
            poseTime = 0f;
            SetPaused(true);
            heading.text = $"{poseIndex + 1}/3  {CurrentPose}";
            body.text = Instruction(CurrentPose);
            SetButtons(("Cancel", Cancel), (null, null), (null, null));
            SetLengthVisible(false);
        }

        void ShowResult()
        {
            SetPaused(false);
            CurrentStep = Step.Result;
            RingFill = 1f;
            if (Calibrator.TryCompute(SettingsStore.Current.hands.calibration, out var result, out var problem))
            {
                Result = result;
                Problem = "";
                heading.text = "Your hand";
                body.text = $"Pinch: thumb to index {result.pinchPinchRatio:0.00} pinched, {result.openPinchRatio:0.00} open -> on below " +
                            $"{result.pinchOn:0.00}, off above {result.pinchOff:0.00}.\nFingers: ratio {result.fistExtension:0.00} in a fist, " +
                            $"{result.openExtension:0.00} open -> curled below {result.curledRatio:0.00}.\nHand length {result.handLength * 100f:0.0} cm " +
                            (result.handLengthMeasured ? "(measured with LiDAR)." : "(kept: no LiDAR depth; set it on the first page).");
                SetButtons(("Save", Save), ("Retry", Begin), ("Cancel", Cancel));
            }
            else
            {
                Result = null;
                Problem = problem;
                heading.text = "Try again";
                body.text = problem;
                SetButtons(("Retry", Begin), ("Cancel", Cancel), (null, null));
            }
        }

        void Fail(string problem)
        {
            Problem = problem;
            ShowIntro(problem);
        }

        void SetPaused(bool value)
        {
            if (paused == value) return;
            paused = value;
            if (hands != null)
                foreach (var hand in GestureRecognizers.Hands) hands.SetSuppressed(hand, value);
            if (gestures != null) gestures.Paused = value;
        }

        void SetButtons(params (string label, Action action)[] set)
        {
            for (int i = 0; i < 3; i++)
            {
                bool shown = set[i].label != null;
                buttons[i].gameObject.SetActive(shown);
                labels[i].gameObject.SetActive(shown);
                labels[i].text = set[i].label ?? "";
                actions[i] = set[i].action;
            }
        }

        void SetLengthVisible(bool visible)
        {
            HandLengthSlider.gameObject.SetActive(visible);
            lengthFill.gameObject.SetActive(visible);
            lengthLabel.gameObject.SetActive(visible);
            RefreshLength();
        }

        void RefreshLength()
        {
            if (lengthLabel == null) return;
            var c = SettingsStore.Current.hands.calibration;
            lengthLabel.text = $"Hand length {c.handLength * 100f:0.0} cm{(c.handLengthMeasured ? " (measured)" : "")}: wrist crease to middle knuckle";
            var track = HandLengthSlider.transform;
            float filled = track.localScale.x * Mathf.InverseLerp(HandCalibration.MinHandLength, HandCalibration.MaxHandLength, c.handLength);
            lengthFill.localPosition = new Vector3(track.localPosition.x - track.localScale.x * 0.5f + filled * 0.5f, track.localPosition.y, -0.002f);
            lengthFill.localScale = new Vector3(Mathf.Max(0.0005f, filled), track.localScale.y, 1f);
        }

        bool TryGetHand(out HandState state)
        {
            state = default;
            if (Source == null) return false;
            if (CurrentStep == Step.Measure)
            {
                state = Source.GetHand(MeasuredHand);
                return state.IsTracked;
            }
            var dominant = SettingsStore.Current.hands.dominantHand;
            var other = dominant == Handedness.Left ? Handedness.Right : Handedness.Left;
            foreach (var hand in new[] { dominant, other })
            {
                state = Source.GetHand(hand);
                if (!state.IsTracked) continue;
                MeasuredHand = hand;
                return true;
            }
            return false;
        }

        void Update()
        {
            if (CurrentStep == Step.Intro || CurrentStep == Step.Result)
            {
                RefreshRing(CurrentStep == Step.Result ? 1f : 0f, RingOn);
                status.text = "";
                return;
            }

            // Keep the pointers paused (a gesture recognizer giving its hand back would otherwise wake one up).
            if (hands != null)
                foreach (var h in GestureRecognizers.Hands)
                    if (!hands.IsSuppressed(h)) hands.SetSuppressed(h, true);
            float dt = Time.deltaTime;
            poseTime += dt;
            bool tracked = TryGetHand(out var hand);
            if (poseTime > PoseTimeout)
            {
                Fail($"Timed out on {CurrentPose.ToString().ToLowerInvariant()}: keep the hand in view of the camera. Nothing was changed.");
                return;
            }

            if (CurrentStep == Step.GetReady)
            {
                // The countdown runs only while a hand is in view.
                stepTime = tracked ? stepTime + dt : 0f;
                RingFill = Mathf.Clamp01(stepTime / GetReadyTime);
                status.text = tracked ? $"Get ready... ({MeasuredHand} hand)" : "Hold your hand up where the camera can see it";
                RefreshRing(RingFill, RingReady);
                if (stepTime >= GetReadyTime)
                {
                    CurrentStep = Step.Measure;
                    Calibrator.Clear(CurrentPose);
                }
                return;
            }

            if (tracked) Calibrator.Add(CurrentPose, hand);
            RingFill = Calibrator.Progress(CurrentPose);
            status.text = tracked ? $"Hold it... ({MeasuredHand} hand)" : "Hand lost: hold it in view to continue";
            RefreshRing(RingFill, RingOn);
            if (!Calibrator.Done(CurrentPose)) return;
            if (++poseIndex < Poses.Length) EnterGetReady();
            else
            {
                poseIndex = Poses.Length - 1;
                ShowResult();
            }
        }

        void RefreshRing(float fill, Color on)
        {
            int lit = Mathf.RoundToInt(fill * RingSegments);
            for (int i = 0; i < ringMaterials.Count; i++) ringMaterials[i].color = i < lit ? on : RingOff;
        }

        protected override void Layout(Vector2 size)
        {
            panel.localPosition = new Vector3(0f, 0f, -0.0005f);
            panel.localScale = new Vector3(size.x, size.y, 1f);
            float left = -size.x * 0.5f + Gap, top = size.y * 0.5f - Gap;

            float ringRadius = Mathf.Min(size.y * 0.22f, size.x * 0.14f);
            float ringX = size.x * 0.5f - Gap - ringRadius - 0.01f, ringY = top - 0.05f - ringRadius;
            ring.localPosition = new Vector3(ringX, ringY, -0.002f);
            for (int i = 0; i < ringSegments.Count; i++)
            {
                // Clockwise from the top.
                float angle = Mathf.PI * 0.5f - i * Mathf.PI * 2f / RingSegments;
                ringSegments[i].localPosition = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * ringRadius;
                ringSegments[i].localRotation = Quaternion.Euler(0f, 0f, angle * Mathf.Rad2Deg);
                ringSegments[i].localScale = new Vector3(ringRadius * 0.12f, ringRadius * 0.24f, 1f);
            }

            float textWidth = size.x - ringRadius * 2f - Gap * 4f;
            heading.transform.localPosition = new Vector3(left + textWidth * 0.5f, top - 0.025f, -0.003f);
            heading.rectTransform.sizeDelta = new Vector2(textWidth, 0.05f);
            float bodyTop = top - 0.06f, bodyHeight = size.y - 0.06f - ButtonHeight * 3f - Gap * 5f;
            body.transform.localPosition = new Vector3(left + textWidth * 0.5f, bodyTop - bodyHeight * 0.5f, -0.003f);
            body.rectTransform.sizeDelta = new Vector2(textWidth, bodyHeight);

            float bottom = -size.y * 0.5f + Gap;
            float buttonY = bottom + ButtonHeight * 0.5f;
            for (int i = 0; i < 3; i++)
            {
                float x = left + ButtonWidth * (i + 0.5f) + Gap * i;
                buttons[i].transform.localPosition = new Vector3(x, buttonY, -0.002f);
                buttons[i].transform.localScale = new Vector3(ButtonWidth, ButtonHeight, 1f);
                labels[i].transform.localPosition = new Vector3(x, buttonY, -0.003f);
                labels[i].rectTransform.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
            }

            float statusY = buttonY + ButtonHeight + Gap;
            status.transform.localPosition = new Vector3(0f, statusY, -0.003f);
            status.rectTransform.sizeDelta = new Vector2(size.x - Gap * 2f, ButtonHeight);
            lengthLabel.transform.localPosition = new Vector3(0f, statusY, -0.003f);
            lengthLabel.rectTransform.sizeDelta = new Vector2(size.x - Gap * 2f, ButtonHeight);
            float sliderY = statusY + ButtonHeight * 0.8f + Gap * 0.5f, sliderWidth = size.x - Gap * 2f;
            HandLengthSlider.transform.localPosition = new Vector3(0f, sliderY, -0.002f);
            HandLengthSlider.transform.localScale = new Vector3(sliderWidth, ButtonHeight * 0.5f, 1f);
            RefreshLength();
        }

        protected override void OnDestroy()
        {
            SetPaused(false);
            base.OnDestroy();
        }
    }
}
