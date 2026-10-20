using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// The consent modal's content (<see cref="ConsentGate"/>): the question, a countdown to the automatic Deny, and
    /// Approve and Deny buttons (hand pinch or mouse). The stereo bar's TRIGGER (ALLOW) and the Mono touch buttons
    /// answer the same question.
    /// </summary>
    public class ConsentContent : WindowContent
    {
        static readonly Color PanelColor = new Color(0.13f, 0.14f, 0.17f);
        static readonly Color ApproveColor = new Color(0.20f, 0.55f, 0.35f);
        static readonly Color DenyColor = new Color(0.60f, 0.25f, 0.25f);
        const float ButtonHeight = 0.045f, ButtonWidth = 0.15f, Gap = 0.012f;

        public WindowButton ApproveButton { get; private set; }
        public WindowButton DenyButton { get; private set; }
        public string Text => question != null ? question.text : "";
        public string Countdown => countdown != null ? countdown.text : "";

        ConsentGate gate;
        Transform panel;
        TextMeshPro question, countdown, approveLabel, denyLabel;

        public void Init(SpatialWindow window, Material baseMaterial, ConsentGate consentGate)
        {
            Attach(window, baseMaterial);
            gate = consentGate;
            panel = CreateQuad("Panel", transform, CreateMaterial(PanelColor));
            // The window itself is the solid panel; this quad only stays as a hit area.
            panel.GetComponent<MeshRenderer>().enabled = false;
            question = CreateLabel("Question", transform, gate.Question, 0.24f, TextAlignmentOptions.TopLeft);
            question.textWrappingMode = TextWrappingModes.Normal;
            question.overflowMode = TextOverflowModes.Truncate;
            countdown = CreateLabel("Countdown", transform, "", 0.18f, TextAlignmentOptions.MidlineRight);
            countdown.color = new Color(0.75f, 0.77f, 0.82f);
            ApproveButton = CreateControl<WindowButton>("Approve", transform, CreateMaterial(ApproveColor));
            approveLabel = CreateLabel("ApproveLabel", transform, "Approve", 0.22f, TextAlignmentOptions.Center);
            DenyButton = CreateControl<WindowButton>("Deny", transform, CreateMaterial(DenyColor));
            denyLabel = CreateLabel("DenyLabel", transform, "Deny", 0.22f, TextAlignmentOptions.Center);
            ApproveButton.Clicked += () => gate.Approve();
            DenyButton.Clicked += () => gate.Deny();
            Relayout();
        }

        void Update()
        {
            if (gate == null) return;
            question.text = gate.Question;
            countdown.text = $"Denies in {gate.SecondsLeft:0} s - TRIGGER = Allow";
        }

        protected override void Layout(Vector2 size)
        {
            panel.localPosition = new Vector3(0f, 0f, -0.0005f);
            panel.localScale = new Vector3(size.x, size.y, 1f);
            float textHeight = size.y - ButtonHeight - Gap * 3f;
            question.transform.localPosition = new Vector3(0f, size.y * 0.5f - Gap - textHeight * 0.5f, -0.003f);
            question.rectTransform.sizeDelta = new Vector2(size.x - Gap * 2f, textHeight);

            float y = -size.y * 0.5f + Gap + ButtonHeight * 0.5f, left = -size.x * 0.5f + Gap;
            ApproveButton.transform.localPosition = new Vector3(left + ButtonWidth * 0.5f, y, -0.002f);
            ApproveButton.transform.localScale = new Vector3(ButtonWidth, ButtonHeight, 1f);
            approveLabel.transform.localPosition = new Vector3(left + ButtonWidth * 0.5f, y, -0.003f);
            approveLabel.rectTransform.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
            float denyX = left + ButtonWidth * 1.5f + Gap;
            DenyButton.transform.localPosition = new Vector3(denyX, y, -0.002f);
            DenyButton.transform.localScale = new Vector3(ButtonWidth, ButtonHeight, 1f);
            denyLabel.transform.localPosition = new Vector3(denyX, y, -0.003f);
            denyLabel.rectTransform.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
            float countdownLeft = denyX + ButtonWidth * 0.5f + Gap, countdownWidth = size.x * 0.5f - Gap - countdownLeft;
            countdown.transform.localPosition = new Vector3(countdownLeft + countdownWidth * 0.5f, y, -0.003f);
            countdown.rectTransform.sizeDelta = new Vector2(Mathf.Max(0.02f, countdownWidth), ButtonHeight);
        }
    }
}
