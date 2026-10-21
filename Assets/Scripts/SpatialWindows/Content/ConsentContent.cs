using SpatialLauncher.SpatialWindows.Theme;
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
        static readonly Color ApproveColor = UITheme.SuccessFill;
        static readonly Color DenyColor = UITheme.DestructiveFill;
        /// <summary>56 pt high buttons, the consent exception in the design spec.</summary>
        public const float ButtonHeight = 0.056f;
        const float ButtonWidth = 0.15f, Gap = 0.012f, CountdownHeight = 0.042f;

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
            question = CreateLabel("Question", transform, gate.Question, TextRole.Headline, TextAlignmentOptions.TopLeft);
            question.textWrappingMode = TextWrappingModes.Normal;
            question.overflowMode = TextOverflowModes.Truncate;
            countdown = CreateLabel("Countdown", transform, "", TextRole.Secondary, TextAlignmentOptions.MidlineLeft);
            countdown.textWrappingMode = TextWrappingModes.Normal; // consent text is never cut off
            countdown.overflowMode = TextOverflowModes.Overflow;
            ApproveButton = CreateControl<WindowButton>("Approve", transform, CreateMaterial(ApproveColor));
            approveLabel = CreateLabel("ApproveLabel", transform, "Approve", TextRole.Headline, TextAlignmentOptions.Center);
            DenyButton = CreateControl<WindowButton>("Deny", transform, CreateMaterial(DenyColor));
            denyLabel = CreateLabel("DenyLabel", transform, "Deny", TextRole.Headline, TextAlignmentOptions.Center);
            // Solid in every state, never disabled; the labels stay white.
            ApproveButton.AlwaysEnabled = true;
            DenyButton.AlwaysEnabled = true;
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
            float textHeight = size.y - ButtonHeight - CountdownHeight - Gap * 4f;
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
            // The countdown has its own full-width row above the buttons.
            float countdownY = y + ButtonHeight * 0.5f + Gap + CountdownHeight * 0.5f;
            countdown.transform.localPosition = new Vector3(0f, countdownY, -0.003f);
            countdown.rectTransform.sizeDelta = new Vector2(Mathf.Max(0.02f, size.x - Gap * 2f), CountdownHeight);
        }
    }
}
