using SpatialLauncher.LaptopLink;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// The pairing screen: shows the code the laptop's <c>agent.py --pair</c> asks for (XXXX-XXXX-XXXX), the seconds left
    /// and how to run the agent. Opening it starts pairing mode on the <see cref="LinkHost"/> (listening, advertising
    /// m=pair); closing it ends pairing. When the code ends it says why and offers "New code". The pinch confirmation
    /// itself is the consent modal (<see cref="ConsentPairConfirmer"/>); the result is shown here.
    /// </summary>
    public class PairingCodeContent : WindowContent
    {
        /// <summary>No panel fill: the texts sit on the glass itself (a raised panel dropped them under 4.5:1).</summary>
        public static readonly Color PanelColor = Color.clear;
        public static readonly Color ButtonColor = UITheme.AccentFill;
        const float CodeFontSize = 0.62f;
        const float ButtonHeight = 0.045f, ButtonWidth = 0.16f, Gap = 0.01f;

        /// <summary>Content size the screen needs.</summary>
        public static readonly Vector2 PreferredSize = new Vector2(0.6f, 0.36f);

        public WindowButton NewCodeButton { get; private set; }
        public string CodeText => codeLabel != null ? codeLabel.text : "";
        public string CountdownText => countdown != null ? countdown.text : "";
        public string StatusText => status != null ? status.text : "";
        public string CommandText => command != null ? command.text : "";
        public bool Expired { get; private set; }
        public bool Paired { get; private set; }
        public bool Confirming { get; private set; }

        LinkHost host;
        Transform panel;
        TextMeshPro title, codeLabel, countdown, command, status, newCodeLabel;

        public void Init(SpatialWindow window, Material baseMaterial, LinkHost linkHost)
        {
            Attach(window, baseMaterial);
            host = linkHost;
            panel = CreateQuad("Panel", transform, CreateMaterial(PanelColor));
            Round(panel, UITheme.ControlRadius);
            title = CreateLabel("Title", transform, "Pair a new laptop", TextRole.Title, TextAlignmentOptions.Center);
            codeLabel = CreateLabel("Code", transform, "", TextRole.LargeTitle, TextAlignmentOptions.Center);
            codeLabel.fontSize = CodeFontSize; // the code is read from across the room
            codeLabel.richText = false;
            countdown = CreateLabel("Countdown", transform, "", TextRole.Secondary, TextAlignmentOptions.Center);
            command = CreateLabel("Command", transform, CommandLines(), TextRole.Body, TextAlignmentOptions.Center);
            command.textWrappingMode = TextWrappingModes.Normal;
            command.richText = false;
            status = CreateLabel("Status", transform, "", TextRole.Body, TextAlignmentOptions.Center);
            status.richText = false;
            NewCodeButton = CreateControl<WindowButton>("NewCode", transform, CreateMaterial(ButtonColor));
            newCodeLabel = CreateLabel("NewCodeLabel", transform, "New code", TextRole.Headline, TextAlignmentOptions.Center);
            NewCodeButton.Clicked += Begin;
            if (host != null)
            {
                host.PairingEnded += OnPairingEnded;
                host.ConnectionFinished += OnFinished;
            }
            Begin();
            Relayout();
        }

        /// <summary>The instructions: the agent command, and in the editor the loopback address to pair with.</summary>
        public static string CommandLines()
        {
            string text = "On the laptop run: agent.py --pair";
#if UNITY_EDITOR
            text += " --headset 127.0.0.1:" + LinkListener.LinkPort;
#endif
            return text;
        }

        /// <summary>Starts pairing mode with a fresh code (also the "New code" button).</summary>
        public void Begin()
        {
            Expired = false;
            Paired = false;
            Confirming = false;
            if (host == null)
            {
                var service = LinkService.Current;
                status.text = service != null && service.Problem != null ? "Pairing is unavailable: " + service.Problem : "Pairing is unavailable.";
                codeLabel.text = "----";
                Refresh();
                return;
            }
            var code = host.BeginPairing();
            codeLabel.text = code != null ? code.Formatted : "----";
            status.text = host.Listener.BindFailed ? host.Listener.Status : "Waiting for the laptop...";
            Refresh();
        }

        void OnPairingEnded(PairCodeEnd reason)
        {
            Expired = true;
            switch (reason)
            {
                case PairCodeEnd.Expired: status.text = "Code expired"; break;
                case PairCodeEnd.StruckOut: status.text = "Too many wrong codes. Start again with a new code."; break;
                case PairCodeEnd.Used: Confirming = true; status.text = "Code used. Confirm on the headset..."; break;
                default: status.text = "Pairing ended"; break;
            }
            Refresh();
        }

        void OnFinished(LinkResult result)
        {
            if (result.Kind != LinkKind.Pairing) return;
            Paired = result.Ok;
            Confirming = false;
            if (result.Ok)
            {
                status.text = "Paired with " + ConsentPairConfirmer.DisplayName(result.LaptopName);
                codeLabel.text = "";
            }
            else status.text = "Pairing failed: " + result.Reason;
            Refresh();
        }

        void Update()
        {
            Refresh();
        }

        void Refresh()
        {
            if (countdown == null) return;
            bool live = host != null && host.Pairing && !Expired;
            int seconds = live ? Mathf.CeilToInt((float)host.Codes.RemainingSeconds) : 0;
            string text = live ? "Expires in " + seconds + " s" : "";
            if (countdown.text != text) countdown.text = text;
            bool showButton = !Paired && !Confirming && (Expired || !live);
            NewCodeButton.gameObject.SetActive(showButton);
            newCodeLabel.gameObject.SetActive(showButton);
        }

        protected override void Layout(Vector2 size)
        {
            panel.localPosition = new Vector3(0f, 0f, -0.0005f);
            panel.localScale = new Vector3(size.x, size.y, 1f);
            float width = size.x - Gap * 2f, top = size.y * 0.5f;
            Row(title, ref top, width, 0.04f);
            Row(codeLabel, ref top, width, 0.1f);
            Row(countdown, ref top, width, 0.03f);
            Row(command, ref top, width, 0.06f);
            Row(status, ref top, width, 0.035f);
            float y = -size.y * 0.5f + Gap + ButtonHeight * 0.5f;
            NewCodeButton.transform.localPosition = new Vector3(0f, y, -0.002f);
            NewCodeButton.transform.localScale = new Vector3(ButtonWidth, ButtonHeight, 1f);
            newCodeLabel.transform.localPosition = new Vector3(0f, y, -0.003f);
            newCodeLabel.rectTransform.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
        }

        static void Row(TextMeshPro label, ref float top, float width, float height)
        {
            top -= Gap * 0.5f;
            label.transform.localPosition = new Vector3(0f, top - height * 0.5f, -0.003f);
            label.rectTransform.sizeDelta = new Vector2(width, height);
            top -= height;
        }

        protected override void OnDestroy()
        {
            if (host != null)
            {
                host.PairingEnded -= OnPairingEnded;
                host.ConnectionFinished -= OnFinished;
                host.EndPairing();
            }
            base.OnDestroy();
        }
    }
}
