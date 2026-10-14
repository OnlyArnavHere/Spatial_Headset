using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// The Laptop picker: connect to the laptop agent (Tools/LaptopAgent) with its address and pairing token, list what
    /// the laptop can stream (the whole desktop first, then its open windows) and open a stream window for each entry
    /// picked; several can be open at once. The list comes over a list-only <see cref="WebSocketRemoteInputSink"/>;
    /// every stream window then opens its own link, so its input goes to its own laptop window.
    /// </summary>
    public class LaptopPickerContent : WindowContent
    {
        static readonly Color PanelColor = new Color(0.13f, 0.14f, 0.17f);
        static readonly Color FieldColor = new Color(0.20f, 0.21f, 0.25f);
        static readonly Color ListColor = new Color(0.20f, 0.55f, 0.35f);
        static readonly Color WindowEntryColor = new Color(0.22f, 0.27f, 0.38f);
        static readonly Color DesktopEntryColor = new Color(0.30f, 0.42f, 0.70f);
        static readonly Color MoreColor = new Color(0.32f, 0.34f, 0.40f);

        /// <summary>Entries shown at once; "More" pages through the rest.</summary>
        public const int RowsPerPage = 6;
        const float RowHeight = 0.04f;
        const float StatusHeight = 0.03f;
        const float Gap = 0.01f;
        const float ButtonWidth = 0.11f;
        const float PortWidth = 0.08f;
        const float TextInset = 0.008f;

        /// <summary>Content height the picker needs: the connect row, the status line, a page of entries and "More".</summary>
        public const float ContentHeight = RowHeight * (RowsPerPage + 2) + StatusHeight + Gap * (RowsPerPage + 2);

        public WindowTextField HostField { get; private set; }
        public WindowTextField PortField { get; private set; }
        public WindowTextField TokenField { get; private set; }
        public WindowButton ListButton { get; private set; }
        public WindowButton MoreButton { get; private set; }
        /// <summary>The entry buttons on the current page, in list order.</summary>
        public IReadOnlyList<WindowButton> EntryButtons => pageButtons;
        /// <summary>The targets on the current page, matching <see cref="EntryButtons"/>.</summary>
        public IReadOnlyList<LaptopTarget> PageTargets => pageTargets;
        /// <summary>Everything the laptop offered in the last list.</summary>
        public IReadOnlyList<LaptopTarget> Targets => link != null ? link.Targets : Array.Empty<LaptopTarget>();
        public WebSocketRemoteInputSink Link => link;
        public string StatusLine => statusLine != null ? statusLine.text : "";
        public int Page { get; private set; }

        RemoteConnectionSettings settings;
        Action<LaptopTarget, LaptopConnection> open;
        WebSocketRemoteInputSink link;
        LaptopConnection connection;
        bool listWanted;
        Transform panel;
        TextMeshPro statusLine, listLabel, moreLabel;
        readonly List<(WindowTextField field, TextMeshPro label)> fields = new List<(WindowTextField, TextMeshPro)>();
        readonly List<(WindowButton button, TextMeshPro label, Material material)> rows = new List<(WindowButton, TextMeshPro, Material)>();
        readonly List<WindowButton> pageButtons = new List<WindowButton>();
        readonly List<LaptopTarget> pageTargets = new List<LaptopTarget>();

        /// <param name="openStream">Opens a stream window for a picked target over the given connection.</param>
        public void Init(SpatialWindow window, Material baseMaterial, RemoteConnectionSettings connectionSettings,
            Action<LaptopTarget, LaptopConnection> openStream)
        {
            Attach(window, baseMaterial);
            settings = connectionSettings;
            open = openStream;
            link = new WebSocketRemoteInputSink();
            link.StatusChanged += OnLinkChanged;
            link.TargetsReceived += OnTargets;

            panel = CreateQuad("ConnectPanel", transform, CreateMaterial(PanelColor));
            HostField = CreateField("Host", "Laptop address");
            PortField = CreateField("Port", "Port");
            TokenField = CreateField("Token", "Pairing token");
            PortField.DigitsOnly = true;
            PortField.MaxLength = 5;
            TokenField.Masked = true;
            var group = new List<WindowTextField> { HostField, PortField, TokenField };
            foreach (var (field, _) in fields)
            {
                field.Group = group;
                field.Submitted += _ => List();
            }
            HostField.SetText(RemoteConnectionPrefs.Host(settings));
            PortField.SetText(RemoteConnectionPrefs.VideoPort(settings).ToString());
            TokenField.SetText(RemoteConnectionPrefs.Token());

            ListButton = CreateControl<WindowButton>("ListButton", transform, CreateMaterial(ListColor));
            listLabel = CreateLabel("ListLabel", transform, "List", 0.22f, TextAlignmentOptions.Center);
            ListButton.Clicked += List;

            statusLine = CreateLabel("StatusLine", transform, "Start agent.py on the laptop, enter its address and token, then List.",
                0.17f, TextAlignmentOptions.MidlineLeft);
            statusLine.color = new Color(0.75f, 0.77f, 0.82f);

            for (int i = 0; i < RowsPerPage; i++)
            {
                var material = CreateMaterial(WindowEntryColor);
                var button = CreateControl<WindowButton>($"Entry {i}", transform, material);
                var label = CreateLabel($"EntryLabel {i}", transform, "", 0.2f, TextAlignmentOptions.MidlineLeft);
                label.richText = false; // window titles are shown as typed, never as markup
                int row = i;
                button.Clicked += () => Pick(row);
                rows.Add((button, label, material));
            }
            MoreButton = CreateControl<WindowButton>("MoreButton", transform, CreateMaterial(MoreColor));
            moreLabel = CreateLabel("MoreLabel", transform, "More", 0.2f, TextAlignmentOptions.Center);
            MoreButton.Clicked += () => ShowPage(Page + 1);

            ShowPage(0);
            Relayout();
        }

        WindowTextField CreateField(string fieldName, string placeholder)
        {
            var material = CreateMaterial(FieldColor);
            var field = CreateControl<WindowTextField>($"{fieldName}Field", transform, material);
            var label = CreateLabel($"{fieldName}Text", transform, "", 0.2f, TextAlignmentOptions.MidlineLeft);
            field.Init(label, material, placeholder);
            fields.Add((field, label));
            return field;
        }

        /// <summary>Saves the panel's values, connects to the agent and asks what the laptop can stream.</summary>
        public void List()
        {
            WindowTextField.BlurAll();
            string host = HostField.Text.Trim();
            string token = TokenField.Text.Trim();
            if (host.Length == 0 || !int.TryParse(PortField.Text, out int port) || port <= 0 || port > 65535 || token.Length == 0)
            {
                statusLine.text = "Enter the laptop's address, port and the token printed by agent.py";
                return;
            }

            RemoteConnectionPrefs.Save(host, port, token);
            connection = new LaptopConnection(host, port, token);
            listWanted = true;
            link.Connect(host, settings != null ? settings.inputPort : 8765, token);
        }

        void OnLinkChanged()
        {
            if (!link.IsConnected)
            {
                statusLine.text = link.StatusText;
                return;
            }
            if (!listWanted) return;
            listWanted = false;
            link.RequestTargets();
            statusLine.text = "Asking the laptop what it can stream…";
        }

        void OnTargets()
        {
            int windows = 0;
            foreach (var target in link.Targets)
                if (target.kind == "window") windows++;
            statusLine.text = $"The whole desktop and {windows} window{(windows == 1 ? "" : "s")}. Pick one to stream it.";
            ShowPage(0);
        }

        void ShowPage(int page)
        {
            var all = Targets;
            int pages = Mathf.Max(1, Mathf.CeilToInt(all.Count / (float)RowsPerPage));
            Page = (page % pages + pages) % pages;
            pageButtons.Clear();
            pageTargets.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                int index = Page * RowsPerPage + i;
                bool used = index < all.Count;
                var (button, label, material) = rows[i];
                button.gameObject.SetActive(used);
                label.gameObject.SetActive(used);
                if (!used) continue;
                var target = all[index];
                label.text = target.kind == "window" && target.width > 0 ? $"{target.title}   ({target.width}x{target.height})" : target.title;
                material.color = target.kind == "window" ? WindowEntryColor : DesktopEntryColor;
                pageButtons.Add(button);
                pageTargets.Add(target);
            }
            bool more = pages > 1;
            MoreButton.gameObject.SetActive(more);
            moreLabel.gameObject.SetActive(more);
            moreLabel.text = $"More ({Page + 1}/{pages})";
        }

        void Pick(int row)
        {
            if (row < pageTargets.Count && connection != null) open?.Invoke(pageTargets[row], connection);
        }

        void Update() => link?.Tick();

        protected override void Layout(Vector2 size)
        {
            float top = size.y * 0.5f;
            float left = -size.x * 0.5f;

            // Connect row and status line on a panel at the top.
            float panelHeight = RowHeight + StatusHeight + Gap * 1.5f;
            panel.localPosition = new Vector3(0f, top - panelHeight * 0.5f, -0.001f);
            panel.localScale = new Vector3(size.x, panelHeight, 1f);

            float rowY = top - Gap * 0.5f - RowHeight * 0.5f;
            float x = left + Gap;
            float textWidth = Mathf.Max(0.04f, (size.x - ButtonWidth - PortWidth - Gap * 5f) * 0.5f);
            float[] widths = { textWidth, PortWidth, textWidth };
            for (int i = 0; i < fields.Count; i++)
            {
                var (field, label) = fields[i];
                float w = widths[i];
                field.transform.localPosition = new Vector3(x + w * 0.5f, rowY, -0.002f);
                field.transform.localScale = new Vector3(w, RowHeight, 1f);
                label.transform.localPosition = new Vector3(x + w * 0.5f, rowY, -0.003f);
                label.rectTransform.sizeDelta = new Vector2(w - TextInset * 2f, RowHeight);
                x += w + Gap;
            }
            ListButton.transform.localPosition = new Vector3(x + ButtonWidth * 0.5f, rowY, -0.002f);
            ListButton.transform.localScale = new Vector3(ButtonWidth, RowHeight, 1f);
            listLabel.transform.localPosition = new Vector3(x + ButtonWidth * 0.5f, rowY, -0.003f);
            listLabel.rectTransform.sizeDelta = new Vector2(ButtonWidth, RowHeight);

            float statusY = rowY - RowHeight * 0.5f - Gap * 0.5f - StatusHeight * 0.5f;
            statusLine.transform.localPosition = new Vector3(0f, statusY, -0.003f);
            statusLine.rectTransform.sizeDelta = new Vector2(size.x - Gap * 2f, StatusHeight);

            // One full-width entry per row below it, then "More".
            float y = top - panelHeight - Gap - RowHeight * 0.5f;
            foreach (var (button, label, _) in rows)
            {
                button.transform.localPosition = new Vector3(0f, y, -0.002f);
                button.transform.localScale = new Vector3(size.x, RowHeight, 1f);
                label.transform.localPosition = new Vector3(0f, y, -0.003f);
                label.rectTransform.sizeDelta = new Vector2(size.x - TextInset * 4f, RowHeight);
                y -= RowHeight + Gap;
            }
            float moreWidth = ButtonWidth * 1.5f;
            MoreButton.transform.localPosition = new Vector3(size.x * 0.5f - moreWidth * 0.5f, y, -0.002f);
            MoreButton.transform.localScale = new Vector3(moreWidth, RowHeight, 1f);
            moreLabel.transform.localPosition = new Vector3(size.x * 0.5f - moreWidth * 0.5f, y, -0.003f);
            moreLabel.rectTransform.sizeDelta = new Vector2(moreWidth, RowHeight);
        }

        protected override void OnDestroy()
        {
            if (link != null)
            {
                link.StatusChanged -= OnLinkChanged;
                link.TargetsReceived -= OnTargets;
                link.Dispose();
            }
            base.OnDestroy();
        }
    }
}
