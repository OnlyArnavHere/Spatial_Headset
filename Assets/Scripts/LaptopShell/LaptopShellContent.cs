using System;
using System.Collections.Generic;
using SpatialLauncher.SpatialWindows;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.LaptopShell
{
    /// <summary>
    /// The Laptop Shell window: the laptop's apps in the headset. From the top: a search field (type on the virtual or a
    /// physical keyboard, or hold the mic and speak), a header with the panel's title and page buttons, the panel (an app
    /// grid, search results, an app's windows, or why the shell is locked), a status line, and the app bar with the
    /// laptop's pinned and running apps, their real icons and running indicators. <see cref="ShellStyle"/> gives it the
    /// laptop's look: a Start-style panel and centred taskbar for Windows, a large search over Applications and a
    /// floating dock for macOS. Tapping an app streams its window (restoring a minimized one) or launches it; holding a
    /// bar icon, or tapping an app with several windows, lists its windows with minimize, restore and close.
    /// Everything goes through <see cref="IShellSession"/>, so nothing reaches the laptop unless the session holds
    /// granted control; until then the panel offers "Allow control" (the usual consent prompt, then the laptop's own).
    /// </summary>
    public class LaptopShellContent : WindowContent
    {
        public static readonly Vector2 PreferredSize = new Vector2(0.70f, 0.50f);
        public static Vector2 WindowSize => PreferredSize + Vector2.one * (Padding * 2f);

        public const int MaxBarSlots = 12;
        public const int MaxGridTiles = 21;
        public const int MaxResultRows = 6;
        public const int MaxWindowRows = 5;
        public const float NoticeSeconds = 6f;
        public const float Gap = 0.008f;
        public const float RowHeight = 0.044f;
        public const float RoundButton = 0.044f;
        const float SectionGap = 0.012f;
        const float HeaderHeight = 0.046f;
        const float StatusHeight = 0.026f;
        const float CaptionHeight = 0.024f;
        const float ToggleWidth = 0.14f;
        const float DividerWidth = 0.016f;
        const float BarMargin = 0.012f;
        const float RowIcon = 0.032f;

        static readonly int UseTexId = Shader.PropertyToID("_UseTex");
        static readonly int TexAlphaId = Shader.PropertyToID("_TexAlpha");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        /// <summary>The bar: the raised glass of controls.</summary>
        public static readonly Color BarColor = UITheme.GlassRaised;
        /// <summary>Hit plates are clear until hovered (the state overlay lights them).</summary>
        static readonly Color ClearPlate = new Color(1f, 1f, 1f, 0f);
        /// <summary>The top search result stands out a little.</summary>
        public static readonly Color TopResultColor = new Color(1f, 1f, 1f, 0.08f);
        static readonly Color[] LetterPalette =
        {
            new Color(0.30f, 0.42f, 0.70f), new Color(0.20f, 0.55f, 0.40f), new Color(0.70f, 0.32f, 0.42f),
            new Color(0.55f, 0.40f, 0.70f), new Color(0.75f, 0.45f, 0.18f), new Color(0.20f, 0.50f, 0.65f),
            new Color(0.45f, 0.47f, 0.52f), new Color(0.62f, 0.30f, 0.25f),
        };

        public enum Panel { Grid, Results, Windows, Locked }

        /// <summary>An app icon: the laptop's own picture once it arrives, a coloured letter tile until then.</summary>
        public sealed class IconView
        {
            public Transform Quad;
            public Material Material;
            public TextMeshPro Letter;
            public string Id, Name;
            public Vector3 Scale = Vector3.one;
            public bool ShowsTexture;
        }

        public sealed class BarSlot
        {
            public ShellIconButton Button;
            public IconView Icon;
            public TextMeshPro Glyph;
            public Transform Indicator;
            public Material IndicatorMaterial;
            public ShellApp App;
            public bool IsAppsButton;
            public bool Focused;
        }

        public sealed class GridTile
        {
            public WindowButton Button;
            public IconView Icon;
            public TextMeshPro Caption;
            public ShellApp App;
        }

        public sealed class ResultRow
        {
            public WindowButton Button;
            public Material Plate;
            public IconView Icon;
            public TextMeshPro Name, Detail;
            public ShellResult Result;
        }

        public sealed class WindowRow
        {
            public WindowButton Open;
            public TextMeshPro Title;
            public WindowButton Toggle;
            public TextMeshPro ToggleGlyph;
            public WindowButton Close;
            public TextMeshPro CloseGlyph;
            public ShellWindow Window;
        }

        public IShellSession Session { get; private set; }
        public LaptopShellClient Client => Session?.Client;
        public ShellStyle Style { get; private set; } = ShellStyle.For(ShellOs.Unknown);
        public Panel Shown { get; private set; } = Panel.Locked;
        public bool ShowingAllApps { get; private set; }
        public int Page { get; private set; }
        public int PageCount { get; private set; } = 1;
        /// <summary>The app whose windows the panel lists, or null.</summary>
        public string WindowsAppId { get; private set; }
        public string StatusText => status != null ? status.text : "";
        public string MessageText => message != null && message.gameObject.activeSelf ? message.text : "";
        public string HeaderText => header != null && header.gameObject.activeSelf ? header.text : "";
        public ShellLock Lock { get; private set; } = ShellLock.Connecting;

        public WindowTextField SearchField { get; private set; }
        public WindowHoldButton MicButton { get; private set; }
        public WindowButton KeyboardButton { get; private set; }
        public WindowButton ToggleButton { get; private set; }
        public WindowButton PreviousButton { get; private set; }
        public WindowButton NextButton { get; private set; }
        public WindowButton AllowButton { get; private set; }
        public Transform Bar => barPlate;
        public Transform Divider => divider;

        /// <summary>The bar slots in use, left to right (the apps button first in taskbar style).</summary>
        public IReadOnlyList<BarSlot> BarSlots => shownBar;
        public IReadOnlyList<GridTile> GridTiles => shownTiles;
        public IReadOnlyList<ResultRow> ResultRows => shownResults;
        public IReadOnlyList<WindowRow> WindowRows => shownWindows;
        /// <summary>Every control of the window, for size checks.</summary>
        public IReadOnlyList<WindowControl> Controls => controls;

        /// <summary>The keyboard button: open the virtual keyboard for the search field (set by the launcher).</summary>
        public event Action KeyboardRequested;

        readonly List<BarSlot> barPool = new List<BarSlot>();
        readonly List<BarSlot> shownBar = new List<BarSlot>();
        readonly List<GridTile> tilePool = new List<GridTile>();
        readonly List<GridTile> shownTiles = new List<GridTile>();
        readonly List<ResultRow> resultPool = new List<ResultRow>();
        readonly List<ResultRow> shownResults = new List<ResultRow>();
        readonly List<WindowRow> windowPool = new List<WindowRow>();
        readonly List<WindowRow> shownWindows = new List<WindowRow>();
        readonly List<WindowControl> controls = new List<WindowControl>();
        readonly List<IconView> icons = new List<IconView>();

        BarSlot appsSlot;
        Transform barPlate, divider;
        int dividerBefore = -1;
        TextMeshPro searchLabel, searchGlyph, micGlyph, keyboardGlyph, header, toggleLabel, pageLabel, message, allowLabel, status;
        TextMeshPro previousGlyph, nextGlyph;
        Material searchMaterial;
        bool dirty = true, iconsDirty;
        int resultCapacity = 4;
        string notice;
        float noticeUntil;
        string shownError;
        float errorUntil;

        /// <summary>Test seam: the clock notices are timed with.</summary>
        public Func<float> Clock = () => Time.unscaledTime;

        public void Init(SpatialWindow window, Material baseMaterial, IShellSession session)
        {
            Attach(window, baseMaterial);
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Session.ShellWindow = window;
            Build();
            var client = Session.Client;
            client.Changed += MarkDirty;
            client.IconReady += OnIconReady;
            client.Notice += OnNotice;
            client.AutoRefresh = true;
            ApplyStyle(ShellStyle.For(client.Os));
            Rebuild();
        }

        // --- Building --------------------------------------------------------------------------------------------------

        void Build()
        {
            searchMaterial = CreateRounded(WindowTextField.NormalColor, UITheme.ControlRadius);
            SearchField = Track(CreateControl<WindowTextField>("SearchField", transform, searchMaterial));
            searchLabel = Label("SearchText", "", TextRole.Body, TextAlignmentOptions.MidlineLeft);
            SearchField.Init(searchLabel, searchMaterial, Style.SearchPlaceholder);
            SearchField.MaxLength = LaptopShellClient.MaxQuery;
            SearchField.TextChanged += OnSearchChanged;
            SearchField.Submitted += OnSearchSubmitted;
            searchGlyph = Glyph("SearchGlyph", UIIcons.Search);
            SearchField.BindLabel(searchGlyph);

            MicButton = Track(CreateControl<WindowHoldButton>("MicButton", transform, CreateRounded(UITheme.GlassRaised, 0f)));
            Round(MicButton.transform, 0f, capsule: true);
            micGlyph = Glyph("MicGlyph", UIIcons.Mic);
            MicButton.BindLabel(micGlyph);
            MicButton.Pressed += OnMicPressed;
            MicButton.Released += OnMicReleased;

            KeyboardButton = Track(CreateControl<WindowButton>("KeyboardButton", transform, CreateRounded(UITheme.GlassRaised, 0f)));
            Round(KeyboardButton.transform, 0f, capsule: true);
            keyboardGlyph = Glyph("KeyboardGlyph", UIIcons.Keyboard);
            KeyboardButton.BindLabel(keyboardGlyph);
            KeyboardButton.Clicked += OnKeyboardClicked;

            header = Label("Header", "", TextRole.Headline, TextAlignmentOptions.MidlineLeft);
            ToggleButton = Track(CreateControl<WindowButton>("ToggleButton", transform, CreateRounded(UITheme.GlassRaised, 0f)));
            Round(ToggleButton.transform, 0f, capsule: true);
            toggleLabel = Label("ToggleLabel", "", TextRole.Body, TextAlignmentOptions.Center);
            ToggleButton.BindLabel(toggleLabel);
            ToggleButton.Clicked += OnToggle;
            PreviousButton = Track(CreateControl<WindowButton>("PreviousPage", transform, CreateRounded(UITheme.GlassRaised, 0f)));
            Round(PreviousButton.transform, 0f, capsule: true);
            previousGlyph = Glyph("PreviousGlyph", UIIcons.ChevronLeft);
            PreviousButton.BindLabel(previousGlyph);
            PreviousButton.Clicked += () => ShowPage(Page - 1);
            NextButton = Track(CreateControl<WindowButton>("NextPage", transform, CreateRounded(UITheme.GlassRaised, 0f)));
            Round(NextButton.transform, 0f, capsule: true);
            nextGlyph = Glyph("NextGlyph", UIIcons.ChevronRight);
            NextButton.BindLabel(nextGlyph);
            NextButton.Clicked += () => ShowPage(Page + 1);
            pageLabel = Label("PageLabel", "", TextRole.Caption, TextAlignmentOptions.Center);

            for (int i = 0; i < MaxGridTiles; i++)
            {
                var tile = new GridTile { Button = Track(CreateControl<WindowButton>($"Tile {i}", transform, CreateRounded(ClearPlate, 16f))) };
                tile.Icon = NewIcon($"TileIcon {i}");
                tile.Caption = Label($"TileCaption {i}", "", TextRole.Caption, TextAlignmentOptions.Center);
                tile.Button.BindLabel(tile.Caption);
                Follow(tile.Button, tile.Icon);
                var bound = tile;
                tile.Button.Clicked += () => { if (bound.App != null) Client.Open(bound.App); };
                tilePool.Add(tile);
            }

            for (int i = 0; i < MaxResultRows; i++)
            {
                var plate = CreateRounded(ClearPlate, UITheme.ControlRadius);
                var row = new ResultRow { Button = Track(CreateControl<WindowButton>($"Result {i}", transform, plate)), Plate = plate };
                row.Icon = NewIcon($"ResultIcon {i}");
                row.Name = Label($"ResultName {i}", "", TextRole.Body, TextAlignmentOptions.MidlineLeft);
                row.Detail = Label($"ResultDetail {i}", "", TextRole.Secondary, TextAlignmentOptions.MidlineRight);
                row.Button.BindLabel(row.Name);
                row.Button.BindLabel(row.Detail);
                Follow(row.Button, row.Icon);
                var bound = row;
                row.Button.Clicked += () => { if (bound.Result != null) Client.Open(bound.Result); };
                resultPool.Add(row);
            }

            for (int i = 0; i < MaxWindowRows; i++)
            {
                var row = new WindowRow { Open = Track(CreateControl<WindowButton>($"Window {i}", transform, CreateRounded(UITheme.GlassRaised, UITheme.ControlRadius))) };
                row.Title = Label($"WindowTitle {i}", "", TextRole.Body, TextAlignmentOptions.MidlineLeft);
                row.Open.BindLabel(row.Title);
                row.Toggle = Track(CreateControl<WindowButton>($"WindowToggle {i}", transform, CreateRounded(UITheme.GlassRaised, 0f)));
                Round(row.Toggle.transform, 0f, capsule: true);
                row.ToggleGlyph = Glyph($"WindowToggleGlyph {i}", UIIcons.Remove);
                row.Toggle.BindLabel(row.ToggleGlyph);
                row.Close = Track(CreateControl<WindowButton>($"WindowClose {i}", transform, CreateRounded(UITheme.GlassRaised, 0f)));
                Round(row.Close.transform, 0f, capsule: true);
                row.CloseGlyph = Glyph($"WindowCloseGlyph {i}", UIIcons.Close);
                row.Close.BindLabel(row.CloseGlyph);
                var bound = row;
                row.Open.Clicked += () => OpenWindow(bound.Window);
                row.Toggle.Clicked += () => ToggleWindow(bound.Window);
                row.Close.Clicked += () => { if (bound.Window != null) Client.Close(bound.Window.Id); };
                windowPool.Add(row);
            }

            message = Label("Message", "", TextRole.Body, TextAlignmentOptions.Center);
            message.textWrappingMode = TextWrappingModes.Normal;
            message.overflowMode = TextOverflowModes.Ellipsis;
            AllowButton = Track(CreateControl<WindowButton>("AllowButton", transform, CreateRounded(UITheme.AccentFill, 0f)));
            Round(AllowButton.transform, 0f, capsule: true);
            allowLabel = Label("AllowLabel", "Allow control", TextRole.Headline, TextAlignmentOptions.Center);
            AllowButton.BindLabel(allowLabel, keepContrast: true);
            AllowButton.Clicked += OnAllow;

            status = Label("Status", "", TextRole.Secondary, TextAlignmentOptions.Center);

            barPlate = CreateQuad("Bar", transform, CreateRounded(BarColor, UITheme.ControlRadius));
            divider = CreateQuad("Divider", transform, CreateRounded(new Color(1f, 1f, 1f, 0.35f), 0f), ControlLayer);
            appsSlot = NewBarSlot("AppsButton");
            appsSlot.IsAppsButton = true;
            appsSlot.Glyph = Glyph("AppsGlyph", UIIcons.Apps);
            appsSlot.Button.BindLabel(appsSlot.Glyph);
            appsSlot.Button.Clicked += ShowStart;
            for (int i = 0; i < MaxBarSlots; i++)
            {
                var slot = NewBarSlot($"BarApp {i}");
                var bound = slot;
                slot.Button.Clicked += () => TapApp(bound.App);
                slot.Button.Held += () => ShowWindows(bound.App);
                barPool.Add(slot);
            }
        }

        BarSlot NewBarSlot(string slotName)
        {
            var slot = new BarSlot { Button = Track(CreateControl<ShellIconButton>(slotName, transform, CreateRounded(ClearPlate, UITheme.ControlRadius))) };
            slot.Icon = NewIcon(slotName + " Icon");
            slot.IndicatorMaterial = CreateRounded(Color.white, 0f);
            slot.Indicator = CreateQuad(slotName + " Indicator", transform, slot.IndicatorMaterial, FillLayer);
            Round(slot.Indicator, 0f, capsule: true);
            Follow(slot.Button, slot.Icon);
            return slot;
        }

        T Track<T>(T control) where T : WindowControl
        {
            controls.Add(control);
            return control;
        }

        IconView NewIcon(string iconName)
        {
            var material = CreateRounded(Color.gray, UITheme.ControlRadius);
            var view = new IconView { Material = material, Quad = CreateQuad(iconName, transform, material, FillLayer) };
            view.Letter = Label(iconName + " Letter", "", TextRole.Headline, TextAlignmentOptions.Center);
            view.Letter.color = Color.white;
            view.Letter.overflowMode = TextOverflowModes.Overflow;
            icons.Add(view);
            return view;
        }

        // The icon (and its letter) shrinks with its button's press, like a home tile's glyph.
        static void Follow(WindowControl button, IconView icon)
        {
            button.VisualRefreshed += () =>
            {
                float scale = button.State.Scale;
                icon.Quad.localScale = new Vector3(icon.Scale.x * scale, icon.Scale.y * scale, 1f);
                icon.Letter.transform.localScale = Vector3.one * scale;
            };
        }

        TextMeshPro Label(string labelName, string text, TextRole role, TextAlignmentOptions alignment)
        {
            var label = CreateLabel(labelName, transform, text, role, alignment);
            label.richText = false; // app names and window titles are shown as typed, never as markup
            return label;
        }

        TextMeshPro Glyph(string glyphName, string icon)
        {
            var glyph = CreateLabel(glyphName, transform, icon, TextRole.Icon, TextAlignmentOptions.Center);
            glyph.overflowMode = TextOverflowModes.Overflow;
            return glyph;
        }

        // --- Style -----------------------------------------------------------------------------------------------------

        void ApplyStyle(ShellStyle style)
        {
            Style = style;
            ThemeApplier.Apply(searchLabel, style.SearchText);
            SearchField.Init(searchLabel, searchMaterial, style.SearchPlaceholder);
            Round(SearchField.transform, style.SearchCapsule ? 0f : style.SearchRadiusPt, style.SearchCapsule);
            Round(barPlate, style.BarRadiusPt);
            if (!style.PinnedFirst) ShowingAllApps = true;
            dirty = true;
        }

        // --- Binding ---------------------------------------------------------------------------------------------------

        void MarkDirty() => dirty = true;

        void OnIconReady(string id) => iconsDirty = true;

        void OnNotice(string text)
        {
            notice = text;
            noticeUntil = Clock() + NoticeSeconds;
            dirty = true;
        }

        /// <summary>Binds the lists to the pools and lays the window out again (called whenever something changed).</summary>
        public void Rebuild()
        {
            dirty = false;
            var client = Client;
            if (client == null) return;
            if (client.Os != ShellOs.Unknown && client.Os != Style.Os) ApplyStyle(ShellStyle.For(client.Os));
            Lock = Session.Lock;
            bool usable = Lock == ShellLock.None;

            if (!usable) Shown = Panel.Locked;
            else if (WindowsAppId != null && WindowsApp() != null) Shown = Panel.Windows;
            else
            {
                WindowsAppId = null;
                Shown = client.Query.Length > 0 ? Panel.Results : Panel.Grid;
            }

            foreach (var control in controls)
                if (control != AllowButton) control.Interactable = usable;
            AllowButton.Interactable = ShellLockText.CanAsk(Lock);
            BindBar(client, usable);
            BindPanel(client); // after: it enables the page buttons only where there is a page to go to
            UpdateStatus();
            Relayout();
        }

        ShellApp WindowsApp()
        {
            var app = Client.Find(WindowsAppId);
            return app != null && app.Windows.Count > 0 ? app : null;
        }

        void BindBar(LaptopShellClient client, bool usable)
        {
            shownBar.Clear();
            int capacity = BarCapacity(ContentSize.x);
            if (Style.AppsButtonInBar)
            {
                shownBar.Add(appsSlot);
                capacity--;
            }
            appsSlot.Button.gameObject.SetActive(Style.AppsButtonInBar);
            appsSlot.Glyph.gameObject.SetActive(Style.AppsButtonInBar);
            appsSlot.Icon.Quad.gameObject.SetActive(false);
            appsSlot.Icon.Letter.gameObject.SetActive(false);
            appsSlot.Indicator.gameObject.SetActive(false);

            var dock = usable || client.Running.Count > 0 || client.Pinned.Count > 0 ? client.DockItems() : new List<ShellApp>();
            dividerBefore = -1;
            bool anyPinned = false;
            for (int i = 0; i < barPool.Count; i++)
            {
                var slot = barPool[i];
                bool used = i < dock.Count && i < capacity;
                slot.Button.gameObject.SetActive(used);
                slot.Icon.Quad.gameObject.SetActive(used);
                if (!used)
                {
                    slot.Icon.Letter.gameObject.SetActive(false);
                    slot.Indicator.gameObject.SetActive(false);
                    slot.App = null;
                    continue;
                }
                var app = dock[i];
                if (app.Pinned) anyPinned = true;
                else if (anyPinned && dividerBefore < 0 && Style.DividerBeforeUnpinned) dividerBefore = shownBar.Count;
                slot.App = app;
                slot.Focused = false;
                foreach (var w in app.Windows)
                    if (w.Focused) slot.Focused = true;
                ShowIcon(slot.Icon, app.Id, app.Name);
                slot.Indicator.gameObject.SetActive(app.Running);
                slot.IndicatorMaterial.SetColor(ColorId, Style.IndicatorColor(slot.Focused));
                shownBar.Add(slot);
            }
            divider.gameObject.SetActive(dividerBefore > 0);
        }

        /// <summary>How many slots fit in the bar at this content width (the apps button included).</summary>
        public int BarCapacity(float width)
        {
            float room = width - BarMargin * 2f - (Style.DividerBeforeUnpinned ? DividerWidth : 0f);
            return Mathf.Clamp(Mathf.FloorToInt(room / Style.BarSlot), 1, MaxBarSlots + (Style.AppsButtonInBar ? 1 : 0));
        }

        void BindPanel(LaptopShellClient client)
        {
            shownTiles.Clear();
            shownResults.Clear();
            shownWindows.Clear();
            int tiles = Shown == Panel.Grid ? Style.GridColumns * Style.GridRows : 0;
            int count = 0;

            List<ShellApp> grid = null;
            if (Shown == Panel.Grid)
            {
                grid = new List<ShellApp>(Style.PinnedFirst && !ShowingAllApps ? client.Pinned : client.Installed);
                if (grid.Count == 0 && Style.PinnedFirst && !ShowingAllApps) grid = new List<ShellApp>(client.Installed);
                count = grid.Count;
                SetPages(count, tiles);
            }
            for (int i = 0; i < tilePool.Count; i++)
            {
                var tile = tilePool[i];
                int index = Page * tiles + i;
                bool used = grid != null && i < tiles && index < grid.Count;
                tile.Button.gameObject.SetActive(used);
                tile.Caption.gameObject.SetActive(used);
                tile.Icon.Quad.gameObject.SetActive(used);
                if (!used)
                {
                    tile.Icon.Letter.gameObject.SetActive(false);
                    tile.App = null;
                    continue;
                }
                tile.App = grid[index];
                tile.Caption.text = tile.App.Name;
                ShowIcon(tile.Icon, tile.App.Id, tile.App.Name);
                shownTiles.Add(tile);
            }

            var results = Shown == Panel.Results ? client.Results : null;
            if (results != null) SetPages(results.Count, resultCapacity);
            for (int i = 0; i < resultPool.Count; i++)
            {
                var row = resultPool[i];
                int index = Page * resultCapacity + i;
                bool used = results != null && i < resultCapacity && index < results.Count;
                row.Button.gameObject.SetActive(used);
                row.Name.gameObject.SetActive(used);
                row.Detail.gameObject.SetActive(used);
                row.Icon.Quad.gameObject.SetActive(used);
                if (!used)
                {
                    row.Icon.Letter.gameObject.SetActive(false);
                    row.Result = null;
                    continue;
                }
                var result = results[index];
                row.Result = result;
                row.Name.text = result.Name;
                string kind = result.Kind == ShellResultKind.App ? (result.Running ? "App, open" : "App") : "Window in " + result.AppName;
                row.Detail.text = index == 0 ? $"{Style.TopResultTitle} · {kind}" : kind;
                row.Plate.SetColor(ColorId, index == 0 ? TopResultColor : ClearPlate);
                ShowIcon(row.Icon, result.Id, result.Kind == ShellResultKind.Window ? result.AppName : result.Name);
                shownResults.Add(row);
            }

            var app = Shown == Panel.Windows ? WindowsApp() : null;
            if (app != null) SetPages(app.Windows.Count, MaxWindowRows);
            for (int i = 0; i < windowPool.Count; i++)
            {
                var row = windowPool[i];
                int index = Page * MaxWindowRows + i;
                bool used = app != null && index < app.Windows.Count;
                row.Open.gameObject.SetActive(used);
                row.Title.gameObject.SetActive(used);
                row.Toggle.gameObject.SetActive(used);
                row.ToggleGlyph.gameObject.SetActive(used);
                row.Close.gameObject.SetActive(used);
                row.CloseGlyph.gameObject.SetActive(used);
                if (!used)
                {
                    row.Window = null;
                    continue;
                }
                var w = app.Windows[index];
                row.Window = w;
                row.Title.text = string.IsNullOrEmpty(w.Title) ? app.Name : w.Title + (w.Minimized ? "  (minimized)" : "");
                row.ToggleGlyph.text = w.Minimized ? UIIcons.OpenInFull : UIIcons.Remove;
                shownWindows.Add(row);
            }
            if (Shown == Panel.Locked) SetPages(0, 1);

            // Header: the panel's title and its toggle.
            header.gameObject.SetActive(Shown != Panel.Locked);
            bool toggle = Shown == Panel.Windows || (Shown == Panel.Grid && Style.PinnedFirst);
            ToggleButton.gameObject.SetActive(toggle);
            toggleLabel.gameObject.SetActive(toggle);
            switch (Shown)
            {
                case Panel.Grid:
                    header.text = Style.PinnedFirst && !ShowingAllApps ? Style.GridTitle : Style.AllAppsTitle;
                    toggleLabel.text = ShowingAllApps ? "Pinned" : "All apps";
                    break;
                case Panel.Results:
                    header.text = "Results";
                    break;
                case Panel.Windows:
                    header.text = app != null ? app.Name : "";
                    toggleLabel.text = "Back";
                    break;
            }
            bool pages = PageCount > 1 && Shown != Panel.Locked;
            PreviousButton.gameObject.SetActive(pages);
            NextButton.gameObject.SetActive(pages);
            previousGlyph.gameObject.SetActive(pages);
            nextGlyph.gameObject.SetActive(pages);
            pageLabel.gameObject.SetActive(pages);
            pageLabel.text = $"{Page + 1} / {PageCount}";
            PreviousButton.Interactable = Page > 0;
            NextButton.Interactable = Page < PageCount - 1;

            // The message: why it's locked, or that nothing matched or arrived yet.
            string text = null;
            if (Shown == Panel.Locked) text = ShellLockText.Describe(Lock, Session.LaptopName);
            else if (Shown == Panel.Results && shownResults.Count == 0)
                text = client.PendingCount > 0 ? "Searching…" : $"No apps or windows match “{client.Query}”.";
            else if (Shown == Panel.Grid && shownTiles.Count == 0)
                text = client.InstalledLoading || client.PendingCount > 0 ? $"Loading {LaptopOf()} apps…" : $"No apps found on {LaptopOf(possessive: false)}.";
            message.gameObject.SetActive(text != null);
            if (text != null) message.text = text;
            bool ask = Shown == Panel.Locked && ShellLockText.CanAsk(Lock);
            AllowButton.gameObject.SetActive(ask);
            allowLabel.gameObject.SetActive(ask);
        }

        string LaptopOf(bool possessive = true)
        {
            string name = string.IsNullOrEmpty(Session.LaptopName) ? "the laptop" : Session.LaptopName;
            return possessive ? (name == "the laptop" ? "the laptop's" : name + "'s") : name;
        }

        void SetPages(int count, int perPage)
        {
            PageCount = Mathf.Max(1, Mathf.CeilToInt(count / (float)Mathf.Max(1, perPage)));
            Page = Mathf.Clamp(Page, 0, PageCount - 1);
        }

        void ShowIcon(IconView view, string id, string label)
        {
            view.Id = id;
            view.Name = label ?? "";
            var texture = id != null ? Client.Icon(id) : null;
            view.ShowsTexture = texture != null;
            if (texture != null)
            {
                view.Material.mainTexture = texture;
                view.Material.SetFloat(UseTexId, 1f);
                view.Material.SetFloat(TexAlphaId, 1f);
                view.Material.SetColor(ColorId, Color.white);
                Round(view.Quad, 0f); // the laptop's icon brings its own shape
                view.Letter.gameObject.SetActive(false);
            }
            else
            {
                view.Material.SetFloat(UseTexId, 0f);
                view.Material.SetFloat(TexAlphaId, 0f);
                view.Material.SetColor(ColorId, LetterColor(view.Name));
                Round(view.Quad, Style.Os == ShellOs.MacOS ? 11f : 8f);
                view.Letter.text = Initial(view.Name);
                view.Letter.gameObject.SetActive(view.Quad.gameObject.activeSelf);
            }
        }

        void RefreshIcons()
        {
            foreach (var view in icons)
                if (view.Quad.gameObject.activeSelf && view.Id != null && !view.ShowsTexture) ShowIcon(view, view.Id, view.Name);
        }

        /// <summary>The letter tile's colour for a name: one of a few, picked by the name, darkened for white text.</summary>
        public static Color LetterColor(string name)
        {
            int hash = 17;
            foreach (char c in name ?? "") hash = hash * 31 + char.ToLowerInvariant(c);
            return LauncherContent.TileColor(LetterPalette[(hash & 0x7fffffff) % LetterPalette.Length]);
        }

        public static string Initial(string name)
        {
            foreach (char c in name ?? "")
                if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
            return "?";
        }

        void UpdateStatus()
        {
            float now = Clock();
            var client = Client;
            if (client.LastError != null && client.LastError != shownError)
            {
                shownError = client.LastError;
                errorUntil = now + NoticeSeconds;
            }
            string text = "";
            if (notice != null && now < noticeUntil) text = notice;
            else if (shownError != null && now < errorUntil) text = shownError;
            else if (Lock == ShellLock.None && client.Running.Count == 0 && client.PendingCount > 0) text = $"Loading {LaptopOf()} apps…";
            if (status.text != text) status.text = text;
        }

        // --- Actions ---------------------------------------------------------------------------------------------------

        /// <summary>A bar icon tapped: an app with several windows lists them; otherwise its window streams or it launches.</summary>
        public void TapApp(ShellApp app)
        {
            if (app == null) return;
            var live = Client.Find(app.Id) ?? app;
            if (live.Windows.Count > 1)
            {
                ShowWindows(live);
                return;
            }
            Client.Open(live);
        }

        /// <summary>Lists an app's windows (holding its bar icon). An app without windows has nothing to list.</summary>
        public void ShowWindows(ShellApp app)
        {
            if (app == null) return;
            var live = Client.Find(app.Id) ?? app;
            if (live.Windows.Count == 0) return;
            WindowsAppId = live.Id;
            Page = 0;
            dirty = true;
            Rebuild();
        }

        void OpenWindow(ShellWindow window)
        {
            if (window == null) return;
            Client.OpenWindow(window.Id, window.Title, window.Minimized);
            WindowsAppId = null;
            Rebuild();
        }

        void ToggleWindow(ShellWindow window)
        {
            if (window == null) return;
            if (window.Minimized) Client.Restore(window.Id);
            else Client.Minimize(window.Id);
        }

        /// <summary>The taskbar's apps button: back to the pinned apps with an empty search, ready to type.</summary>
        public void ShowStart()
        {
            WindowsAppId = null;
            ShowingAllApps = !Style.PinnedFirst;
            Page = 0;
            SearchField.SetText("");
            SearchField.Focus();
            Rebuild();
        }

        void OnToggle()
        {
            if (Shown == Panel.Windows) WindowsAppId = null;
            else ShowingAllApps = !ShowingAllApps;
            Page = 0;
            Rebuild();
        }

        void ShowPage(int page)
        {
            Page = Mathf.Clamp(page, 0, PageCount - 1);
            Rebuild();
        }

        void OnSearchChanged(WindowTextField field)
        {
            Client.Search(field.Text);
            WindowsAppId = null;
            Page = 0;
            dirty = true;
        }

        void OnSearchSubmitted(WindowTextField field)
        {
            var results = Client.Results;
            if (Shown == Panel.Results && results.Count > 0) Client.Open(results[0]);
        }

        void OnMicPressed()
        {
            SearchField.Focus();
            var voice = VoiceTyping.Current;
            if (voice == null)
            {
                OnNotice("Voice typing isn't available here.");
                return;
            }
            voice.StartListening();
        }

        void OnMicReleased() => VoiceTyping.Current?.StopListening();

        void OnKeyboardClicked()
        {
            SearchField.Focus();
            KeyboardRequested?.Invoke();
        }

        void OnAllow()
        {
            if (Session.RequestControl()) dirty = true;
        }

        void Update()
        {
            if (Session == null) return;
            Session.Tick();
            if (Session.Lock != Lock) dirty = true;
            if (dirty) Rebuild();
            if (iconsDirty)
            {
                iconsDirty = false;
                RefreshIcons();
            }
            if (status != null && Client != null) UpdateStatus();
        }

        // --- Layout ----------------------------------------------------------------------------------------------------

        protected override void Layout(Vector2 size)
        {
            if (Session == null) return;
            float top = size.y * 0.5f, bottom = -size.y * 0.5f, left = -size.x * 0.5f, right = size.x * 0.5f;

            // Search
            float searchWidth = size.x * Style.SearchWidth, searchHeight = Style.SearchHeight;
            float searchY = top - searchHeight * 0.5f;
            Place(SearchField.transform, 0f, searchY, -0.001f, searchWidth, searchHeight);
            float glyphX = -searchWidth * 0.5f + searchHeight * 0.5f + 0.004f;
            PlaceText(searchGlyph, glyphX, searchY, -0.003f, searchHeight, searchHeight);
            float buttonY = searchY;
            float keyboardX = searchWidth * 0.5f - RoundButton * 0.5f - (searchHeight - RoundButton) * 0.5f;
            float micX = keyboardX - RoundButton - Gap;
            Place(KeyboardButton.transform, keyboardX, buttonY, -0.002f, RoundButton, RoundButton);
            PlaceText(keyboardGlyph, keyboardX, buttonY, -0.004f, RoundButton, RoundButton);
            Place(MicButton.transform, micX, buttonY, -0.002f, RoundButton, RoundButton);
            PlaceText(micGlyph, micX, buttonY, -0.004f, RoundButton, RoundButton);
            float textLeft = glyphX + searchHeight * 0.5f, textRight = micX - RoundButton * 0.5f - Gap;
            PlaceText(searchLabel, (textLeft + textRight) * 0.5f, searchY, -0.003f, Mathf.Max(0.01f, textRight - textLeft), searchHeight);

            // Header: title left; toggle, then the page buttons, right.
            float headerY = top - searchHeight - SectionGap - HeaderHeight * 0.5f;
            PlaceText(header, left + size.x * 0.25f, headerY, -0.003f, size.x * 0.5f, HeaderHeight);
            float nextX = right - RoundButton * 0.5f;
            float pageX = nextX - RoundButton * 0.5f - 0.03f;
            float previousX = pageX - 0.03f - RoundButton * 0.5f;
            Place(NextButton.transform, nextX, headerY, -0.002f, RoundButton, RoundButton);
            PlaceText(nextGlyph, nextX, headerY, -0.004f, RoundButton, RoundButton);
            Place(PreviousButton.transform, previousX, headerY, -0.002f, RoundButton, RoundButton);
            PlaceText(previousGlyph, previousX, headerY, -0.004f, RoundButton, RoundButton);
            PlaceText(pageLabel, pageX, headerY, -0.003f, 0.06f, HeaderHeight);
            float toggleRight = PageCount > 1 ? previousX - RoundButton * 0.5f - Gap : right;
            float toggleX = toggleRight - ToggleWidth * 0.5f;
            Place(ToggleButton.transform, toggleX, headerY, -0.002f, ToggleWidth, RowHeight);
            PlaceText(toggleLabel, toggleX, headerY, -0.003f, ToggleWidth, RowHeight);

            // Bar
            float barHeight = Style.BarHeight, barY = bottom + barHeight * 0.5f;
            int slots = shownBar.Count;
            bool hasDivider = dividerBefore > 0;
            float content = slots * Style.BarSlot + (hasDivider ? DividerWidth : 0f);
            float barWidth = Style.BarFullWidth ? size.x : Mathf.Min(size.x, content + BarMargin * 2f);
            Place(barPlate, 0f, barY, -0.0005f, barWidth, barHeight);
            float x = -content * 0.5f + Style.BarSlot * 0.5f;
            for (int i = 0; i < slots; i++)
            {
                if (hasDivider && i == dividerBefore)
                {
                    Place(divider, x - Style.BarSlot * 0.5f + DividerWidth * 0.5f, barY, -0.001f, 0.002f, Style.BarIcon);
                    x += DividerWidth;
                }
                var slot = shownBar[i];
                Place(slot.Button.transform, x, barY, -0.001f, Style.BarSlot - Gap, barHeight - Gap);
                float iconY = barY + 0.004f;
                PlaceIcon(slot.Icon, x, iconY, Style.BarIcon);
                if (slot.Glyph != null) PlaceText(slot.Glyph, x, iconY, -0.004f, Style.BarIcon, Style.BarIcon);
                var indicator = Style.IndicatorSize(slot.Focused);
                Place(slot.Indicator, x, bottom + Gap * 0.75f + indicator.y * 0.5f, -0.002f, indicator.x, indicator.y);
                x += Style.BarSlot;
            }

            // Status line above the bar
            float statusY = bottom + barHeight + Gap + StatusHeight * 0.5f;
            PlaceText(status, 0f, statusY, -0.003f, size.x, StatusHeight);

            // Panel between the header and the status line
            float bodyTop = headerY - HeaderHeight * 0.5f - Gap;
            float bodyBottom = statusY + StatusHeight * 0.5f + Gap;
            float bodyHeight = Mathf.Max(0.05f, bodyTop - bodyBottom);
            float bodyY = (bodyTop + bodyBottom) * 0.5f;

            int columns = Style.GridColumns, rows = Style.GridRows;
            float cellWidth = size.x / columns, cellHeight = bodyHeight / rows;
            float icon = Mathf.Max(0.01f, Mathf.Min(Style.GridIcon, cellHeight - CaptionHeight - 0.012f));
            for (int i = 0; i < tilePool.Count; i++)
            {
                var tile = tilePool[i];
                int row = i / columns, column = i % columns;
                float cx = left + cellWidth * (column + 0.5f), cy = bodyTop - cellHeight * (row + 0.5f);
                Place(tile.Button.transform, cx, cy, -0.001f, cellWidth - Gap, cellHeight - Gap);
                float iconY = cy + CaptionHeight * 0.5f;
                PlaceIcon(tile.Icon, cx, iconY, icon);
                PlaceText(tile.Caption, cx, iconY - icon * 0.5f - CaptionHeight * 0.5f - 0.002f, -0.003f, cellWidth - Gap, CaptionHeight);
            }

            int capacity = Mathf.Clamp(Mathf.FloorToInt((bodyHeight + Gap) / (RowHeight + Gap)), 1, MaxResultRows);
            if (capacity != resultCapacity)
            {
                resultCapacity = capacity;
                dirty = true; // rebind with the new number of rows next frame
            }
            for (int i = 0; i < resultPool.Count; i++)
            {
                var row = resultPool[i];
                float y = bodyTop - RowHeight * 0.5f - i * (RowHeight + Gap);
                Place(row.Button.transform, 0f, y, -0.001f, size.x, RowHeight);
                float iconX = left + Gap + RowIcon * 0.5f;
                PlaceIcon(row.Icon, iconX, y, RowIcon);
                float nameLeft = iconX + RowIcon * 0.5f + Gap;
                PlaceText(row.Name, nameLeft + size.x * 0.27f, y, -0.003f, size.x * 0.54f, RowHeight);
                PlaceText(row.Detail, right - Gap - size.x * 0.17f, y, -0.003f, size.x * 0.34f, RowHeight);
            }

            for (int i = 0; i < windowPool.Count; i++)
            {
                var row = windowPool[i];
                float y = bodyTop - RowHeight * 0.5f - i * (RowHeight + Gap);
                float closeX = right - RoundButton * 0.5f;
                float toggleWindowX = closeX - RoundButton - Gap;
                float openWidth = size.x - (RoundButton + Gap) * 2f;
                Place(row.Open.transform, left + openWidth * 0.5f, y, -0.001f, openWidth, RowHeight);
                PlaceText(row.Title, left + openWidth * 0.5f, y, -0.003f, openWidth - Gap * 3f, RowHeight);
                Place(row.Toggle.transform, toggleWindowX, y, -0.001f, RoundButton, RoundButton);
                PlaceText(row.ToggleGlyph, toggleWindowX, y, -0.003f, RoundButton, RoundButton);
                Place(row.Close.transform, closeX, y, -0.001f, RoundButton, RoundButton);
                PlaceText(row.CloseGlyph, closeX, y, -0.003f, RoundButton, RoundButton);
            }

            bool ask = AllowButton.gameObject.activeSelf;
            PlaceText(message, 0f, bodyY + (ask ? 0.035f : 0f), -0.003f, size.x * 0.86f, Mathf.Min(bodyHeight, 0.1f));
            Place(AllowButton.transform, 0f, bodyY - 0.05f, -0.002f, 0.24f, RowHeight);
            PlaceText(allowLabel, 0f, bodyY - 0.05f, -0.004f, 0.24f, RowHeight);
        }

        static void Place(Transform quad, float x, float y, float z, float width, float height)
        {
            quad.localPosition = new Vector3(x, y, z);
            quad.localScale = new Vector3(width, height, 1f);
        }

        static void PlaceText(TextMeshPro text, float x, float y, float z, float width, float height)
        {
            if (text == null) return;
            text.transform.localPosition = new Vector3(x, y, z);
            text.rectTransform.sizeDelta = new Vector2(width, height);
        }

        static void PlaceIcon(IconView view, float x, float y, float size)
        {
            view.Scale = new Vector3(size, size, 1f);
            Place(view.Quad, x, y, -0.002f, size, size);
            PlaceText(view.Letter, x, y, -0.004f, size, size);
        }

        protected override void OnDestroy()
        {
            if (Session != null)
            {
                var client = Session.Client;
                client.Changed -= MarkDirty;
                client.IconReady -= OnIconReady;
                client.Notice -= OnNotice;
                Session.Dispose();
            }
            base.OnDestroy();
        }
    }
}
