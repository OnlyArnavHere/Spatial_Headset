using System;
using System.Collections.Generic;
using SpatialLauncher.LaptopLink;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// The Laptop picker, over the headset's WebRTC link (needs SPATIAL_LAUNCHER_WEBRTC): the picker lists the paired laptops; tapping
    /// one is the view-only consent. It then waits for that laptop's agent to dial in ("Waiting for ... to connect"),
    /// shows "Connected (view only)" and the laptop's targets, and opens a stream window per target picked, bound to a
    /// slot of the one session. When the session ends it goes back to the list with the reason. The live indicator and the
    /// panic stop cover the session itself (LinkAnswerer), so they hold with no windows open.
    /// </summary>
    public class LaptopPickerContent : WindowContent
    {
        static readonly Color PanelColor = new Color(0.13f, 0.14f, 0.17f);
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
        const float TextInset = 0.008f;

        /// <summary>Content height the picker needs: the connect row, the status line, a page of entries and "More".</summary>
        public const float ContentHeight = RowHeight * (RowsPerPage + 2) + StatusHeight + Gap * (RowsPerPage + 2);

        public WindowButton MoreButton { get; private set; }
        /// <summary>Opens the pairing screen (the code a laptop's agent.py --pair asks for).</summary>
        public WindowButton PairButton { get; private set; }
        /// <summary>The entry buttons on the current page, in list order.</summary>
        public IReadOnlyList<WindowButton> EntryButtons => pageButtons;
        /// <summary>The targets on the current page, matching <see cref="EntryButtons"/>.</summary>
        public IReadOnlyList<LaptopTarget> PageTargets => pageTargets;
        /// <summary>Everything the laptop offered in the last list.</summary>
        public IReadOnlyList<LaptopTarget> Targets => Mode == PickerMode.Connected ? linkTargets : Array.Empty<LaptopTarget>();
        public string StatusLine => statusLine != null ? statusLine.text : "";
        public int Page { get; private set; }
        public enum PickerMode { List, Waiting, Connected }
        public PickerMode Mode { get; private set; }
        /// <summary>The laptop the picker is waiting for, or connected to; null in the list.</summary>
        public string LaptopName { get; private set; }
        public const string NoWebRtcText = "This build has no WebRTC";

#if SPATIAL_LAUNCHER_WEBRTC
        /// <summary>Opens a stream window for an opened target on a slot of the session (set by the launcher).</summary>
        public Action<LinkSession, OpenedWindow, LaptopTarget> OpenLinked;
        LinkSession linkSession;
        LinkAnswerer answerer;
#endif
        readonly struct Item
        {
            public readonly string Label;
            public readonly Color Color;
            public readonly Action Action;
            public readonly LaptopTarget Target;
            public Item(string label, Color color, Action action, LaptopTarget target = null)
            {
                Label = label;
                Color = color;
                Action = action;
                Target = target;
            }
        }

        readonly List<Item> items = new List<Item>();
        IReadOnlyList<LaptopTarget> linkTargets = Array.Empty<LaptopTarget>();
        readonly HashSet<string> requestedOpens = new HashSet<string>();
        ConsentGate panicGate;
        volatile bool pairsDirty;
        PairStore pairStore;
        TextMeshPro messageLabel;

        Transform panel;
        TextMeshPro statusLine, moreLabel, pairLabel;
        Action pairRequested;
        IDisposable discovery;
        readonly List<(WindowButton button, TextMeshPro label, Material material)> rows = new List<(WindowButton, TextMeshPro, Material)>();
        readonly List<WindowButton> pageButtons = new List<WindowButton>();
        readonly List<LaptopTarget> pageTargets = new List<LaptopTarget>();

        public void Init(SpatialWindow window, Material baseMaterial, Action openPairing = null)
        {
            Attach(window, baseMaterial);
            pairRequested = openPairing;

            panel = CreateQuad("ConnectPanel", transform, CreateMaterial(PanelColor));

            statusLine = CreateLabel("StatusLine", transform, "",
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
            PairButton = CreateControl<WindowButton>("PairButton", transform, CreateMaterial(ListColor));
            pairLabel = CreateLabel("PairLabel", transform, "Pair new laptop", 0.2f, TextAlignmentOptions.Center);
            PairButton.Clicked += () => pairRequested?.Invoke();
            PairButton.gameObject.SetActive(pairRequested != null);
            pairLabel.gameObject.SetActive(pairRequested != null);
            messageLabel = CreateLabel("Message", transform, "", 0.2f, TextAlignmentOptions.Center);
            messageLabel.textWrappingMode = TextWrappingModes.Normal;
            messageLabel.color = new Color(0.75f, 0.77f, 0.82f);
            messageLabel.richText = false;
            messageLabel.gameObject.SetActive(false);

            var linkHost = LinkService.Current?.Host;
            if (linkHost != null)
            {
                pairStore = linkHost.Pairs;
                pairStore.Changed += OnPairsChanged;
            }
#if SPATIAL_LAUNCHER_WEBRTC
            answerer = LinkService.Current?.Answerer;
            if (answerer != null)
            {
                answerer.SessionStarted += OnSessionStarted;
                if (answerer.Session != null) Adopt(answerer.Session);
            }
            panicGate = ConsentGate.Current;
            if (panicGate != null) panicGate.Panicked += OnPanicked;
#endif
            if (Mode == PickerMode.List) ShowList();
            Relayout();
        }

        // PairStore.Changed can come from a worker (a pairing in progress): refresh on the main thread.
        void OnPairsChanged() => pairsDirty = true;

        /// <summary>The paired laptops, or why there is nothing to connect to.</summary>
        void ShowList(string note = null)
        {
            Mode = PickerMode.List;
            LaptopName = null;
            items.Clear();
            string status = note;
#if SPATIAL_LAUNCHER_WEBRTC
            if (answerer == null) status = NoWebRtcText + (note != null ? " (" + note + ")" : "");
            else if (pairStore == null) status = "The laptop link is unavailable";
            else
            {
                IReadOnlyList<PairedLaptop> pairs = null;
                try { pairs = pairStore.List(); }
                catch (Exception e) { status = "Couldn't read the paired laptops: " + e.Message; }
                if (pairs != null)
                {
                    foreach (var pair in pairs)
                    {
                        var laptop = pair;
                        string fingerprint = laptop.Fingerprint != null && laptop.Fingerprint.Length > 8 ? laptop.Fingerprint.Substring(0, 8) : laptop.Fingerprint;
                        items.Add(new Item($"{laptop.Name}   ({fingerprint})", WindowEntryColor, () => Connect(laptop)));
                    }
                    if (status == null)
                        status = pairs.Count == 0 ? "No paired laptops yet. Tap Pair new laptop." : "Tap a laptop to connect (view only).";
                }
            }
#else
            status = NoWebRtcText;
#endif
            statusLine.text = status ?? "";
            messageLabel.gameObject.SetActive(false);
            ShowPage(0);
        }

#if SPATIAL_LAUNCHER_WEBRTC
        // Tapping a paired laptop is the consent for a view-only session: expect it and advertise until it dials in.
        void Connect(PairedLaptop laptop)
        {
            var linkHost = LinkService.Current?.Host;
            if (answerer == null || linkHost == null || Mode != PickerMode.List) return;
            answerer.ExpectLaptop(laptop.PublicKey);
            discovery?.Dispose();
            discovery = linkHost.AcquireDiscovery();
            Mode = PickerMode.Waiting;
            LaptopName = laptop.Name;
            items.Clear();
            items.Add(new Item("Cancel", MoreColor, () => CancelWaiting()));
            string run = "agent.py";
#if UNITY_EDITOR
            run += " --headset 127.0.0.1:47810";
#endif
            statusLine.text = $"Waiting for {laptop.Name} to connect.";
            messageLabel.text = $"Waiting for {laptop.Name} to connect. On the laptop run: {run}";
            messageLabel.gameObject.SetActive(true);
            ShowPage(0);
        }

        void CancelWaiting(string note = null)
        {
            if (Mode != PickerMode.Waiting) return;
            answerer?.StopExpecting();
            discovery?.Dispose();
            discovery = null;
            ShowList(note);
        }

        // A panic stop cancels a wait; the answerer refuses offers until the next Connect.
        void OnPanicked()
        {
            if (this != null) CancelWaiting("Stopped");
        }

        void OnSessionStarted(LinkSession session)
        {
            if (this == null || Mode != PickerMode.Waiting || session == null) return;
            answerer.StopExpecting();
            discovery?.Dispose(); // the session holds its own listener lease
            discovery = null;
            Adopt(session);
        }

        void Adopt(LinkSession session)
        {
            if (session.IsEnded) return;
            linkSession = session;
            Mode = PickerMode.Connected;
            LaptopName = session.LaptopName;
            requestedOpens.Clear();
            linkTargets = session.Control.Targets;
            session.Control.Welcomed += OnWelcomed;
            session.Control.TargetsReceived += OnLinkTargets;
            session.Control.Opened += OnLinkOpened;
            session.Control.OpenFailed += OnOpenFailed;
            session.Ended += OnSessionEnded;
            messageLabel.gameObject.SetActive(false);
            statusLine.text = "Connected (view only)";
            if (session.Control.IsLive) session.Control.RequestTargets();
            RebuildTargetItems();
        }

        void OnWelcomed()
        {
            if (linkSession != null) linkSession.Control.RequestTargets();
        }

        void OnLinkTargets()
        {
            if (linkSession == null) return;
            linkTargets = linkSession.Control.Targets;
            RebuildTargetItems();
        }

        void RebuildTargetItems()
        {
            items.Clear();
            foreach (var target in linkTargets)
            {
                var t = target;
                string title = string.IsNullOrEmpty(t.title) ? t.id : t.title;
                string label = t.kind == "window" && t.width > 0 ? $"{title}   ({t.width}x{t.height})" : title;
                items.Add(new Item(label, t.kind == "window" ? WindowEntryColor : DesktopEntryColor, () => OpenTarget(t), t));
            }
            ShowPage(Page);
        }

        void OpenTarget(LaptopTarget target)
        {
            if (linkSession == null || string.IsNullOrEmpty(target.id)) return;
            requestedOpens.Add(target.id);
            statusLine.text = $"Opening {target.title}…";
            linkSession.Control.Open(target.id);
        }

        void OnLinkOpened(OpenedWindow opened)
        {
            if (linkSession == null || !requestedOpens.Remove(opened.Id)) return; // asked for by another picker
            LaptopTarget known = null;
            foreach (var t in linkSession.Control.Targets)
                if (t.id == opened.Id) known = t;
            var target = new LaptopTarget
            {
                id = opened.Id, path = opened.Path, kind = opened.Kind ?? known?.kind,
                title = known != null && !string.IsNullOrEmpty(known.title) ? known.title : opened.Description,
                description = opened.Description, width = opened.Width, height = opened.Height,
            };
            statusLine.text = "Connected (view only)";
            OpenLinked?.Invoke(linkSession, opened, target);
        }

        void OnOpenFailed(string reason)
        {
            if (linkSession == null) return;
            requestedOpens.Clear(); // the failure doesn't say which target
            statusLine.text = $"Couldn't open it: {reason}";
        }

        void OnSessionEnded(string reason)
        {
            Release();
            ShowList($"Disconnected: {reason}");
        }

        // Drops the session handlers; the session itself is not ended.
        void Release()
        {
            if (linkSession != null)
            {
                linkSession.Control.Welcomed -= OnWelcomed;
                linkSession.Control.TargetsReceived -= OnLinkTargets;
                linkSession.Control.Opened -= OnLinkOpened;
                linkSession.Control.OpenFailed -= OnOpenFailed;
                linkSession.Ended -= OnSessionEnded;
                linkSession = null;
            }
            linkTargets = Array.Empty<LaptopTarget>();
            requestedOpens.Clear();
        }
#endif

        void ShowPage(int page)
        {
            int pages = Mathf.Max(1, Mathf.CeilToInt(items.Count / (float)RowsPerPage));
            Page = (page % pages + pages) % pages;
            pageButtons.Clear();
            pageTargets.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                int index = Page * RowsPerPage + i;
                bool used = index < items.Count;
                var (button, label, material) = rows[i];
                button.gameObject.SetActive(used);
                label.gameObject.SetActive(used);
                if (!used) continue;
                var item = items[index];
                label.text = item.Label;
                material.color = item.Color;
                pageButtons.Add(button);
                pageTargets.Add(item.Target);
            }
            bool more = pages > 1;
            MoreButton.gameObject.SetActive(more);
            moreLabel.gameObject.SetActive(more);
            moreLabel.text = $"More ({Page + 1}/{pages})";
        }

        void Pick(int row)
        {
            int index = Page * RowsPerPage + row;
            if (index < items.Count) items[index].Action?.Invoke();
        }

        void Update()
        {
            if (pairsDirty)
            {
                pairsDirty = false;
                if (Mode == PickerMode.List) ShowList();
            }
        }

        protected override void Layout(Vector2 size)
        {
            float top = size.y * 0.5f;
            float left = -size.x * 0.5f;

            // A status line on a panel at the top.
            float panelHeight = RowHeight + StatusHeight + Gap * 1.5f;
            panel.localPosition = new Vector3(0f, top - panelHeight * 0.5f, -0.001f);
            panel.localScale = new Vector3(size.x, panelHeight, 1f);

            float rowY = top - Gap * 0.5f - RowHeight * 0.5f;
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
            float messageY = top - panelHeight - Gap - RowHeight * 0.5f - (RowHeight + Gap) * 2f;
            messageLabel.transform.localPosition = new Vector3(0f, messageY, -0.003f);
            messageLabel.rectTransform.sizeDelta = new Vector2(size.x - TextInset * 4f, RowHeight * 3f);
            float moreWidth = ButtonWidth * 1.5f;
            MoreButton.transform.localPosition = new Vector3(size.x * 0.5f - moreWidth * 0.5f, y, -0.002f);
            MoreButton.transform.localScale = new Vector3(moreWidth, RowHeight, 1f);
            moreLabel.transform.localPosition = new Vector3(size.x * 0.5f - moreWidth * 0.5f, y, -0.003f);
            moreLabel.rectTransform.sizeDelta = new Vector2(moreWidth, RowHeight);
            float pairWidth = ButtonWidth * 1.8f;
            PairButton.transform.localPosition = new Vector3(left + pairWidth * 0.5f, y, -0.002f);
            PairButton.transform.localScale = new Vector3(pairWidth, RowHeight, 1f);
            pairLabel.transform.localPosition = new Vector3(left + pairWidth * 0.5f, y, -0.003f);
            pairLabel.rectTransform.sizeDelta = new Vector2(pairWidth, RowHeight);
        }

        protected override void OnDestroy()
        {
            discovery?.Dispose();
            discovery = null;
            if (pairStore != null) pairStore.Changed -= OnPairsChanged;
#if SPATIAL_LAUNCHER_WEBRTC
            if (answerer != null)
            {
                answerer.SessionStarted -= OnSessionStarted;
                if (Mode == PickerMode.Waiting) answerer.StopExpecting();
            }
            if (panicGate != null) panicGate.Panicked -= OnPanicked;
            Release();
#endif
            base.OnDestroy();
        }
    }
}
