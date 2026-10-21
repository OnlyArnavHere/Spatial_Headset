using System;
using System.Collections.Generic;
using SpatialLauncher.LaptopLink;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// The Settings window: section tabs on the left (Laptop, Display, Hands, Gestures, Windows, Immersion, Voice, About) and the
    /// selected section's rows on the right. Every change goes into <see cref="SettingsStore"/>, which
    /// <see cref="SettingsService"/> applies and saves. Rows are built from a few kinds: a read-only value, a slider, a
    /// choice of buttons, an on/off toggle, a text field and buttons. Wi-Fi and Bluetooth are shown, never changed.
    /// </summary>
    public class SettingsContent : WindowContent
    {
        /// <summary>No opaque panel behind the rows: the window's glass shows through.</summary>
        public static readonly Color PanelColor = new Color(0f, 0f, 0f, 0f);
        /// <summary>An unselected sidebar row or segment is transparent; the selected one is AccentFill.</summary>
        public static readonly Color TabColor = new Color(1f, 1f, 1f, 0f);
        public static readonly Color SelectedColor = UITheme.AccentFill;
        public static readonly Color TrackColor = UITheme.GlassRaised;
        public static readonly Color FieldColor = WindowTextField.NormalColor;
        public static readonly Color OnColor = UITheme.AccentFill;
        public static readonly Color RemoveColor = UITheme.DestructiveFill;

        /// <summary>Sidebar rows are 160 x 44 pt capsules; every control row is 44 pt high.</summary>
        public const float TabWidth = 0.16f, TabHeight = 0.044f, RowHeight = 0.044f;
        /// <summary>The on/off switch: 52 x 32 pt with a 28 pt knob.</summary>
        public const float SwitchWidth = 0.052f, SwitchHeight = 0.032f, SwitchKnob = 0.028f;
        const float Gap = 0.008f;
        const float LabelWidth = 0.17f;
        const float HeaderHeight = 0.045f;
        /// <summary>Paired laptops listed in Settings > Laptop (each with a Remove button); more show as "+N more".</summary>
        public const int PairedSlots = 3;

        public static readonly string[] SectionNames = { "Laptop", "Display", "Hands", "Gestures", "Windows", "Immersion", "Voice", "About" };

        /// <summary>Content size the window needs.</summary>
        public static readonly Vector2 PreferredSize = new Vector2(0.86f, 0.66f);

        sealed class Row
        {
            public string Label;
            public GameObject Root;
            public Action<float, float, float> Layout; // (centre y, left x, width)
            public Action Refresh;
            /// <summary>Height of the row for a width, when it is taller than <see cref="RowHeight"/> (wrapped text); null for one line.</summary>
            public Func<float, float> Height;
            /// <summary>Clickable parts by name ("Slider", "On", an option's text...), for tests and tools.</summary>
            public readonly Dictionary<string, WindowControl> Controls = new Dictionary<string, WindowControl>();
        }

        sealed class Section
        {
            public string Name;
            public GameObject Root;
            public WindowButton Tab;
            public Material TabMaterial;
            public TextMeshPro TabLabel;
            public readonly List<Row> Rows = new List<Row>();
        }

        public SettingsService Service { get; private set; }
        public string SelectedSection => sections[selected].Name;
        public IReadOnlyList<WindowButton> Tabs => tabs;

        readonly List<Section> sections = new List<Section>();
        readonly List<WindowButton> tabs = new List<WindowButton>();
        Transform panel;
        TextMeshPro header;
        int selected;
        float nextRefresh;
        Section building;

        static SpatialSettings S => SettingsStore.Current;

        public void Init(SpatialWindow window, Material baseMaterial, SettingsService service)
        {
            Attach(window, baseMaterial);
            Service = service;
            panel = CreateQuad("SettingsPanel", transform, CreateRounded(PanelColor, UITheme.ControlRadius));
            header = CreateLabel("Header", transform, "", TextRole.Title, TextAlignmentOptions.MidlineLeft);

            foreach (var name in SectionNames)
            {
                var section = new Section { Name = name, Root = new GameObject($"Section {name}") };
                section.Root.transform.SetParent(transform, false);
                section.TabMaterial = CreateRounded(TabColor, TabHeight * 500f);
                section.Tab = CreateControl<WindowButton>($"Tab {name}", transform, section.TabMaterial);
                Round(section.Tab.transform, 0f, true);
                section.TabLabel = CreateLabel($"TabLabel {name}", transform, name, TextRole.Headline, TextAlignmentOptions.Center);
                section.Tab.BindLabel(section.TabLabel);
                int index = sections.Count;
                section.Tab.Clicked += () => Select(index);
                sections.Add(section);
                tabs.Add(section.Tab);
            }

            BuildLaptop(sections[0]);
            BuildDisplay(sections[1]);
            BuildHands(sections[2]);
            BuildGestures(sections[3]);
            BuildWindows(sections[4]);
            BuildImmersion(sections[5]);
            BuildVoice(sections[6]);
            BuildAbout(sections[7]);

            var pairs = LinkService.Current != null && LinkService.Current.Host != null ? LinkService.Current.Host : null;
            if (pairs != null)
            {
                linkHost = pairs;
                pairs.Pairs.Changed += OnPairsChanged;
                pairList = pairs.Pairs.List();
            }

            Select(0);
        }

        LinkHost linkHost;
        System.Collections.Generic.IReadOnlyList<PairedLaptop> pairList; // cached: refreshed on Init and on Changed

        // PairStore.Changed can come from a worker (a pairing in progress): refresh on the main thread.
        void OnPairsChanged()
        {
            if (linkHost == null || linkHost.Queue == null) return;
            linkHost.Queue.Enqueue(() =>
            {
                if (this == null) return;
                pairList = linkHost.Pairs.List();
                RefreshRows();
            });
        }

        void PairedRow(int slot)
        {
            const float buttonWidth = 0.1f;
            var row = NewRow(slot == 0 ? "Paired laptops" : "Paired #" + (slot + 1));
            var text = CreateLabel("Value", row.Root.transform, "", TextRole.Body, TextAlignmentOptions.MidlineLeft);
            text.richText = false; // laptop names are shown as typed, never as markup
            var button = CreateControl<WindowButton>("Remove", row.Root.transform, CreateRounded(RemoveColor, UITheme.ControlRadius));
            var buttonText = CreateLabel("RemoveLabel", row.Root.transform, "Remove", TextRole.Headline, TextAlignmentOptions.Center);
            button.BindLabel(buttonText);
            row.Controls["Remove"] = button;
            string current = null;
            button.Clicked += () =>
            {
                if (current != null && linkHost != null) linkHost.Pairs.Remove(current);
                row.Refresh();
            };
            var layoutLabel = row.Layout;
            row.Layout = (y, left, width) =>
            {
                layoutLabel(y, left, width);
                float textWidth = width - LabelWidth - Gap * 2f - buttonWidth;
                Place(text.transform, text, left + LabelWidth + Gap + textWidth * 0.5f, y, textWidth, RowHeight);
                float x = left + width - buttonWidth * 0.5f;
                button.transform.localPosition = new Vector3(x, y, -0.001f);
                button.transform.localScale = new Vector3(buttonWidth, RowHeight, 1f);
                Place(buttonText.transform, buttonText, x, y, buttonWidth, RowHeight);
            };
            row.Refresh = () =>
            {
                var list = pairList;
                var item = list != null && slot < list.Count ? list[slot] : null;
                current = item?.Name;
                button.gameObject.SetActive(item != null);
                buttonText.gameObject.SetActive(item != null);
                if (item == null)
                {
                    SetText(text, slot == 0 ? (linkHost == null ? "Pairing unavailable" : "None. Open Laptop > Pair new laptop.") : "");
                    return;
                }
                string more = slot == PairedSlots - 1 && list.Count > PairedSlots ? $"  (+{list.Count - PairedSlots} more)" : "";
                SetText(text, $"{ConsentPairConfirmer.DisplayName(item.Name)}   {PairedLine(item)}{more}");
            };
        }

        /// <summary>"AAAA-BBBB  2026-10-19": the first half of the key fingerprint and the day it was paired.</summary>
        public static string PairedLine(PairedLaptop pair)
        {
            string fp = pair.Fingerprint ?? "";
            if (fp.Length >= 8) fp = fp.Substring(0, 4) + "-" + fp.Substring(4, 4);
            string day = pair.Added != null && pair.Added.Length >= 10 ? pair.Added.Substring(0, 10) : pair.Added ?? "";
            return fp + "  " + day;
        }

        /// <summary>Shows a section by index (as its tab does).</summary>
        public void Select(int index)
        {
            selected = Mathf.Clamp(index, 0, sections.Count - 1);
            for (int i = 0; i < sections.Count; i++)
            {
                sections[i].Root.SetActive(i == selected);
                sections[i].TabMaterial.color = i == selected ? SelectedColor : TabColor;
            }
            header.text = sections[selected].Name;
            WindowTextField.BlurAll();
            RefreshRows();
            Relayout();
        }

        public void Select(string name) => Select(Array.IndexOf(SectionNames, name));

        /// <summary>A clickable part of a row in a section: "Slider", an option's text, "On" (toggles), "Field", a button's text.</summary>
        public WindowControl Control(string section, string rowLabel, string part)
        {
            foreach (var s in sections)
            {
                if (s.Name != section) continue;
                foreach (var row in s.Rows)
                    if (row.Label == rowLabel && row.Controls.TryGetValue(part, out var control)) return control;
            }
            return null;
        }

        /// <summary>The text a row currently shows as its value (value rows and slider readouts).</summary>
        public string ValueText(string section, string rowLabel)
        {
            foreach (var s in sections)
                if (s.Name == section)
                    foreach (var row in s.Rows)
                        if (row.Label == rowLabel)
                        {
                            var value = row.Root.transform.Find("Value");
                            return value != null ? value.GetComponent<TextMeshPro>().text : null;
                        }
            return null;
        }

        void Update()
        {
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + 0.2f;
            RefreshRows();
        }

        void RefreshRows()
        {
            foreach (var row in sections[selected].Rows) row.Refresh?.Invoke();
        }

        // --- Sections ----------------------------------------------------------------------------------------------

        void BuildLaptop(Section s)
        {
            building = s;
            Buttons("Connection", ("Open Laptop picker", () =>
            {
                WindowTextField.BlurAll();
                FindFirstObjectByType<LauncherWindow>()?.OpenLaptopPicker();
            }, () => "Open Laptop picker"));
            Value("Status", LinkStatus);
            Value("Network", () => DeviceStatus.Network);
            Choice("Link", new[] { "Wi-Fi", "Wired (USB)" }, () => S.laptop.wired ? 1 : 0,
                i => SettingsStore.Update(x => x.laptop.wired = i == 1));
            Value("Wired help", () => S.laptop.wired ? UsbHotspot.Help + " " + HotspotState()
                : "Wi-Fi: phone and laptop on the same network. Wired (USB): Personal Hotspot over the cable.");
            for (int i = 0; i < PairedSlots; i++) PairedRow(i);
        }

        /// <summary>The headset's side of the link: listening for a laptop, or not.</summary>
        public static string LinkStatus()
        {
            var host = LinkService.Current != null ? LinkService.Current.Host : null;
            if (host == null) return "The laptop link is unavailable";
            return host.Listener.IsListening ? "Listening for a laptop (open Laptop and pick a paired one)" : "Not listening";
        }

        /// <summary>Whether the phone's Personal Hotspot is on.</summary>
        public static string HotspotState() => UsbHotspot.PhoneHotspotAddress() != null ? "Hotspot on." : "Hotspot off.";

        void BuildDisplay(Section s)
        {
            building = s;
            Slider("Brightness", 0.1f, 1f, () => S.display.brightness, v => SettingsStore.Update(x => x.display.brightness = v),
                v => $"{Mathf.RoundToInt(v * 100f)}%" + (Application.isEditor ? " (phone only)" : ""));
            Slider("Lens spacing (IPD)", 54f, 74f, () => S.display.ipdMm, v => SettingsStore.Update(x => x.display.ipdMm = Mathf.Round(v * 2f) / 2f),
                v => $"{v:0.0} mm");
            Slider("Viewport offset X", -10f, 10f, () => S.display.viewportOffsetMm.x,
                v => SettingsStore.Update(x => x.display.viewportOffsetMm.x = Mathf.Round(v * 2f) / 2f), v => $"{v:+0.0;-0.0;0} mm");
            Slider("Viewport offset Y", -10f, 10f, () => S.display.viewportOffsetMm.y,
                v => SettingsStore.Update(x => x.display.viewportOffsetMm.y = Mathf.Round(v * 2f) / 2f), v => $"{v:+0.0;-0.0;0} mm");
            Value("Note", () => "IPD and offsets tune HoloKit's stereo view for your eyes and the printed helmet.");
            Slider("Passthrough zoom", StereoPassthrough.MinZoom, StereoPassthrough.MaxZoom, () => S.display.passthroughZoom,
                v => SettingsStore.Update(x => x.display.passthroughZoom = Mathf.Round(v * 20f) / 20f), v => $"{v:0.00}x");
            Choice("Zoom preset", DisplaySettings.ZoomPresetNames, () => DisplaySettings.ZoomPresetIndex(S.display.passthroughZoom),
                i => SettingsStore.Update(x => x.display.passthroughZoom = DisplaySettings.ZoomPresets[i]));
            Value("Field of view", PassthroughView);
        }

        string PassthroughView()
        {
            var passthrough = Service != null ? Service.GetComponent<StereoPassthrough>() : null;
            if (passthrough != null && passthrough.ImageFov is Vector2 image && passthrough.CameraFov is Vector2 camera)
                return $"Image {image.x:0}° x {image.y:0}° (camera {camera.x:0}° x {camera.y:0}°). 1.00x is real size.";
            return "1.00x is real size; Wide fits more of the room. F5, or ZOOM in Stereo, steps the presets.";
        }

        void BuildHands(Section s)
        {
            building = s;
            Choice("Dominant hand", new[] { "Left", "Right" }, () => (int)S.hands.dominantHand,
                i => SettingsStore.Update(x => x.hands.dominantHand = (Handedness)i));
            Slider("Sensitivity", 0.5f, 2f, () => S.hands.sensitivity, v => SettingsStore.Update(x => x.hands.sensitivity = Mathf.Round(v * 20f) / 20f),
                v => $"{v:0.00}x");
            Toggle("Pinch", () => S.hands.pinch, v => SettingsStore.Update(x => x.hands.pinch = v));
            Choice("Tracking rate", HandSettings.VisionRateLabels,
                () => Array.IndexOf(HandSettings.VisionFrameIntervals, S.hands.visionFrameInterval),
                i => SettingsStore.Update(x => x.hands.visionFrameInterval = HandSettings.VisionFrameIntervals[i]));
            Value("Heat", () => HandSettings.HeatNote(S.hands.visionFrameInterval));
            Choice("Pointer", Enum.GetNames(typeof(PointerMode)), () => (int)S.hands.pointerMode,
                i => SettingsStore.Update(x => x.hands.pointerMode = (PointerMode)i));
            Slider("Relative speed", 0.5f, 3f, () => S.hands.relativeGain,
                v => SettingsStore.Update(x => x.hands.relativeGain = Mathf.Round(v * 10f) / 10f),
                v => $"{v:0.0}x" + (S.hands.pointerMode == PointerMode.Relative ? "" : " (relative only)"));
            Buttons("Hand setup", ("Calibrate", () =>
            {
                var launcher = Service != null ? Service.GetComponent<LauncherWindow>() : null;
                if (launcher != null) launcher.OpenHandSetup();
            }, null), ("Reset", () => SettingsStore.Update(x => x.hands.calibration = new HandCalibration()), null));
            Value("Calibration", () => S.hands.calibration.Summary);
        }

        void BuildGestures(Section s)
        {
            building = s;
            Toggle("Fist grab", () => S.hands.fistGrab, v => SettingsStore.Update(x => x.hands.fistGrab = v));
            Toggle("Palm push/pull", () => S.hands.palmPush, v => SettingsStore.Update(x => x.hands.palmPush = v));
            Toggle("Two-hand stretch", () => S.hands.twoHandStretch, v => SettingsStore.Update(x => x.hands.twoHandStretch = v));
            Toggle("Cross to hide all", () => S.hands.crossSweepHide, v => SettingsStore.Update(x => x.hands.crossSweepHide = v));
            Toggle("Debug overlay", () => S.hands.debugOverlay, v => SettingsStore.Update(x => x.hands.debugOverlay = v));
            Value("Note", () => "While a remote screen is live, cross and sweep always stops it (panic stop), even when off here.");
        }

        void BuildWindows(Section s)
        {
            building = s;
            Slider("Default curve", 0f, 1f, () => S.windows.defaultCurve, v => SettingsStore.Update(x => x.windows.defaultCurve = v),
                v => v <= 0f ? "Flat" : $"{Mathf.RoundToInt(v * 100f)}%");
            var presets = new List<AspectPreset> { AspectPreset.Free };
            presets.AddRange(AspectPresets.All);
            Choice("Default aspect", presets.ConvertAll(AspectPresets.Label).ToArray(), () => presets.IndexOf(S.windows.defaultAspect),
                i => SettingsStore.Update(x => x.windows.defaultAspect = presets[i]));
            Value("Note", () => "Defaults apply to new windows. Each window's ... button changes its own curve and aspect.");
        }

        void BuildImmersion(Section s)
        {
            building = s;
            Choice("Environment", Enum.GetNames(typeof(ImmersiveEnvironment)), () => (int)S.immersion.environment,
                i => SettingsStore.Update(x => x.immersion.environment = (ImmersiveEnvironment)i));
            Slider("Level", 0f, 1f, () => Service != null && Service.Immersion != null ? Service.Immersion.Level : S.immersion.level,
                v =>
                {
                    v = ImmersionDialContent.SnapToEnds(v);
                    if (Service != null && Service.Immersion != null) Service.Immersion.SetLevel(v);
                    else SettingsStore.Update(x => x.immersion.level = v);
                },
                v => v <= 0f ? "Passthrough" : $"{Mathf.RoundToInt(v * 100f)}%");
            Toggle("Resume on start", () => S.immersion.resumeOnStart, v => SettingsStore.Update(x => x.immersion.resumeOnStart = v));
        }

        void BuildVoice(Section s)
        {
            building = s;
            Choice("Language", VoiceSettings.Languages, () => Array.IndexOf(VoiceSettings.Languages, S.voice.language),
                i => SettingsStore.Update(x => x.voice.language = VoiceSettings.Languages[i]));
            Toggle("On-device only", () => S.voice.preferOnDevice, v => SettingsStore.Update(x => x.voice.preferOnDevice = v));
            Value("Note", () => "Voice typing: hold Talk on the keyboard (iPhone). Speech stays on the phone when the language supports it.");
        }

        void BuildAbout(Section s)
        {
            building = s;
            Value("Battery", () => DeviceStatus.Battery);
            Value("Thermal state", () => DeviceStatus.ThermalState);
            Value("Wi-Fi", () => DeviceStatus.Network);
            Value("Bluetooth", () => DeviceStatus.BluetoothNote);
            Value("Device", () => DeviceStatus.Device);
            Value("Versions", () => DeviceStatus.Versions);
            Value("Settings file", () => SettingsStore.Path);
        }

        // --- Row kinds ---------------------------------------------------------------------------------------------

        Row NewRow(string label)
        {
            var row = new Row { Label = label, Root = new GameObject($"Row {label}") };
            row.Root.transform.SetParent(building.Root.transform, false);
            var title = CreateLabel("Label", row.Root.transform, label, TextRole.Secondary, TextAlignmentOptions.MidlineLeft);
            row.Layout = (y, left, width) => Place(title.transform, title, left + LabelWidth * 0.5f, y, LabelWidth, RowHeight);
            building.Rows.Add(row);
            return row;
        }

        void Value(string label, Func<string> value)
        {
            var row = NewRow(label);
            var text = CreateLabel("Value", row.Root.transform, "", TextRole.Body, TextAlignmentOptions.TopLeft);
            text.textWrappingMode = TextWrappingModes.Normal; // long sentences wrap and grow the row instead of ending in "..."
            text.overflowMode = TextOverflowModes.Overflow;
            float lastHeight = RowHeight;
            row.Height = width => Mathf.Max(RowHeight, text.GetPreferredValues(text.text, width - LabelWidth - Gap, 0f).y);
            var layoutLabel = row.Layout;
            row.Layout = (y, left, width) =>
            {
                layoutLabel(y, left, width);
                float h = row.Height(width), top = y + RowHeight * 0.5f;
                bool wrapped = h > RowHeight + 1e-4f;
                text.alignment = wrapped ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft;
                // A wrapped value starts level with the row's title.
                float inset = wrapped ? (RowHeight - text.fontSize / ThemeApplier.TmpWorldScale * UITheme.LineHeight) * 0.5f : 0f;
                Place(text.transform, text, left + LabelWidth + Gap + (width - LabelWidth - Gap) * 0.5f, top - h * 0.5f - inset, width - LabelWidth - Gap, h);
            };
            row.Refresh = () =>
            {
                SetText(text, value());
                if (row.Height != null && Mathf.Abs(row.Height(lastWidth) - lastHeight) > 1e-4f) { lastHeight = row.Height(lastWidth); Relayout(); }
            };
        }

        void Slider(string label, float min, float max, Func<float> get, Action<float> set, Func<float, string> format)
        {
            const float valueWidth = 0.12f;
            var row = NewRow(label);
            var slider = CreateControl<WindowSlider>("Slider", row.Root.transform, CreateRounded(TrackColor, 0f));
            var fill = CreateQuad("Fill", row.Root.transform, CreateRounded(SelectedColor, 0f), FillLayer);
            Round(fill, 0f, true);
            var text = CreateLabel("Value", row.Root.transform, "", TextRole.Body, TextAlignmentOptions.MidlineRight);
            slider.ValueChanged += t => { set(Mathf.Lerp(min, max, t)); row.Refresh(); };
            row.Controls["Slider"] = slider;
            var layoutLabel = row.Layout;
            row.Layout = (y, left, width) =>
            {
                layoutLabel(y, left, width);
                float trackWidth = Mathf.Max(0.02f, width - LabelWidth - valueWidth - Gap * 2f);
                float trackX = left + LabelWidth + Gap + trackWidth * 0.5f;
                slider.transform.localPosition = new Vector3(trackX, y, -0.001f);
                slider.transform.localScale = new Vector3(trackWidth, RowHeight * 0.7f, 1f);
                Place(text.transform, text, left + width - valueWidth * 0.5f, y, valueWidth, RowHeight);
                row.Refresh();
            };
            row.Refresh = () =>
            {
                float value = get();
                SetText(text, format(value));
                var track = slider.transform;
                float filled = track.localScale.x * Mathf.InverseLerp(min, max, value);
                fill.localPosition = new Vector3(track.localPosition.x - track.localScale.x * 0.5f + filled * 0.5f, track.localPosition.y, -0.002f);
                fill.localScale = new Vector3(Mathf.Max(0.0005f, filled), track.localScale.y, 1f);
            };
        }

        void Choice(string label, string[] options, Func<int> get, Action<int> set)
        {
            var row = NewRow(label);
            var buttons = new List<(WindowButton button, Material material, TextMeshPro text)>();
            // One raised capsule track; the selected option is an AccentFill capsule segment on it.
            var track = CreateQuad("Track", row.Root.transform, CreateRounded(TrackColor, 0f));
            Round(track, 0f, true);
            for (int i = 0; i < options.Length; i++)
            {
                var material = CreateRounded(TabColor, 0f);
                var button = CreateControl<WindowButton>($"Option {options[i]}", row.Root.transform, material);
                Round(button.transform, 0f, true);
                var text = CreateLabel($"OptionLabel {options[i]}", row.Root.transform, options[i], TextRole.Body, TextAlignmentOptions.Center);
                button.BindLabel(text);
                int index = i;
                button.Clicked += () => { set(index); row.Refresh(); };
                row.Controls[options[i]] = button;
                buttons.Add((button, material, text));
            }
            var layoutLabel = row.Layout;
            row.Layout = (y, left, width) =>
            {
                layoutLabel(y, left, width);
                float area = width - LabelWidth - Gap;
                float cell = (area - Gap * (buttons.Count - 1)) / buttons.Count;
                track.localPosition = new Vector3(left + LabelWidth + Gap + area * 0.5f, y, -0.0005f);
                track.localScale = new Vector3(area, RowHeight, 1f);
                for (int i = 0; i < buttons.Count; i++)
                {
                    float x = left + LabelWidth + Gap + cell * (i + 0.5f) + Gap * i;
                    buttons[i].button.transform.localPosition = new Vector3(x, y, -0.001f);
                    buttons[i].button.transform.localScale = new Vector3(cell, RowHeight, 1f);
                    Place(buttons[i].text.transform, buttons[i].text, x, y, cell, RowHeight);
                }
            };
            row.Refresh = () =>
            {
                int current = get();
                for (int i = 0; i < buttons.Count; i++) buttons[i].material.color = i == current ? SelectedColor : TabColor;
            };
        }

        void Toggle(string label, Func<bool> get, Action<bool> set)
        {
            var row = NewRow(label);
            // The hit area is a clear 52 x 44 button; the switch (track and knob) is drawn over it.
            var button = CreateControl<WindowButton>("Toggle", row.Root.transform, CreateRounded(TabColor, 0f));
            var trackMaterial = CreateRounded(TrackColor, 0f);
            var track = CreateQuad("SwitchTrack", row.Root.transform, trackMaterial, ControlLayer);
            Round(track, 0f, true);
            var knob = CreateQuad("SwitchKnob", row.Root.transform, CreateRounded(Color.white, 0f), FillLayer);
            Round(knob, 0f, true);
            var text = CreateLabel("ToggleLabel", row.Root.transform, "", TextRole.Body, TextAlignmentOptions.MidlineLeft);
            // The switch itself shows the state; the clear hit area behind it doesn't.
            button.OwnShapeReacts = false;
            button.BindRenderer(track.GetComponent<MeshRenderer>());
            button.BindRenderer(knob.GetComponent<MeshRenderer>());
            button.BindLabel(text);
            button.Clicked += () => { set(!get()); row.Refresh(); };
            row.Controls["On"] = button;
            var layoutLabel = row.Layout;
            row.Layout = (y, left, rowWidth) =>
            {
                layoutLabel(y, left, rowWidth);
                float x = left + LabelWidth + Gap + SwitchWidth * 0.5f;
                button.transform.localPosition = new Vector3(x, y, -0.001f);
                button.transform.localScale = new Vector3(SwitchWidth, RowHeight, 1f);
                track.localPosition = new Vector3(x, y, -0.0015f);
                track.localScale = new Vector3(SwitchWidth, SwitchHeight, 1f);
                knob.localScale = new Vector3(SwitchKnob, SwitchKnob, 1f);
                Place(text.transform, text, x + SwitchWidth * 0.5f + Gap + 0.03f, y, 0.06f, RowHeight);
                row.Refresh();
            };
            row.Refresh = () =>
            {
                bool on = get();
                SetText(text, on ? "On" : "Off");
                trackMaterial.color = on ? OnColor : TrackColor;
                float travel = (SwitchWidth - SwitchKnob) * 0.5f - 0.002f;
                var t = track.localPosition;
                knob.localPosition = new Vector3(t.x + (on ? travel : -travel), t.y, -0.002f);
            };
        }

        void Field(string label, Func<string> get, Action<string> set, bool masked, bool digits, string placeholder)
        {
            var row = NewRow(label);
            var material = CreateRounded(FieldColor, UITheme.ControlRadius);
            var field = CreateControl<WindowTextField>("Field", row.Root.transform, material);
            var text = CreateLabel("FieldText", row.Root.transform, "", TextRole.Body, TextAlignmentOptions.MidlineLeft);
            field.Init(text, material, placeholder);
            field.Masked = masked;
            field.DigitsOnly = digits;
            if (digits) field.MaxLength = 5;
            field.TextChanged += f => set(f.Text);
            row.Controls["Field"] = field;
            var layoutLabel = row.Layout;
            row.Layout = (y, left, width) =>
            {
                layoutLabel(y, left, width);
                float fieldWidth = width - LabelWidth - Gap;
                float x = left + LabelWidth + Gap + fieldWidth * 0.5f;
                field.transform.localPosition = new Vector3(x, y, -0.001f);
                field.transform.localScale = new Vector3(fieldWidth, RowHeight, 1f);
                Place(text.transform, text, x, y, fieldWidth - 0.016f, RowHeight);
            };
            row.Refresh = () =>
            {
                if (WindowTextField.Focused != field && field.Text != get()) field.SetText(get());
            };
        }

        void Buttons(string label, params (string name, Action click, Func<string> text)[] buttons)
        {
            const float minWidth = 0.14f, padding = 0.016f; // 16 pt of padding on each side of a label
            var row = NewRow(label);
            var made = new List<(WindowButton button, TextMeshPro text, Func<string> caption)>();
            foreach (var (name, click, caption) in buttons)
            {
                var button = CreateControl<WindowButton>($"Button {name}", row.Root.transform, CreateRounded(OnColor, UITheme.ControlRadius));
                var text = CreateLabel($"ButtonLabel {name}", row.Root.transform, name, TextRole.Headline, TextAlignmentOptions.Center);
                button.BindLabel(text);
                button.Clicked += () => { click(); row.Refresh(); };
                row.Controls[name] = button;
                made.Add((button, text, caption));
            }
            var layoutLabel = row.Layout;
            row.Layout = (y, left, rowWidth) =>
            {
                layoutLabel(y, left, rowWidth);
                float used = 0f;
                for (int i = 0; i < made.Count; i++)
                {
                    float width = Mathf.Max(minWidth, made[i].text.GetPreferredValues(made[i].text.text).x + padding * 2f);
                    float x = left + LabelWidth + Gap + used + width * 0.5f;
                    used += width + Gap;
                    made[i].button.transform.localPosition = new Vector3(x, y, -0.001f);
                    made[i].button.transform.localScale = new Vector3(width, RowHeight, 1f);
                    Place(made[i].text.transform, made[i].text, x, y, width, RowHeight);
                }
            };
            row.Refresh = () =>
            {
                foreach (var (_, text, caption) in made)
                    if (caption != null) SetText(text, caption());
            };
        }

        float lastWidth = 0.6f; // width the rows were last laid out at

        static void Place(Transform t, TextMeshPro text, float x, float y, float width, float height)
        {
            t.localPosition = new Vector3(x, y, -0.003f);
            text.rectTransform.sizeDelta = new Vector2(Mathf.Max(0.01f, width), height);
        }

        static void SetText(TextMeshPro text, string value)
        {
            if (text.text != value) text.text = value;
        }

        protected override void OnDestroy()
        {
            if (linkHost != null) linkHost.Pairs.Changed -= OnPairsChanged;
            linkHost = null;
            base.OnDestroy();
        }

        protected override void Layout(Vector2 size)
        {
            panel.localPosition = new Vector3(0f, 0f, -0.0003f);
            panel.localScale = new Vector3(size.x, size.y, 1f);

            float top = size.y * 0.5f;
            float left = -size.x * 0.5f;
            float tabHeight = Mathf.Min(TabHeight, (size.y - Gap * (sections.Count + 1)) / sections.Count);
            for (int i = 0; i < sections.Count; i++)
            {
                float y = top - Gap - tabHeight * (i + 0.5f) - Gap * i;
                var tab = sections[i].Tab.transform;
                tab.localPosition = new Vector3(left + Gap + TabWidth * 0.5f, y, -0.001f);
                tab.localScale = new Vector3(TabWidth, tabHeight, 1f);
                Place(sections[i].TabLabel.transform, sections[i].TabLabel, left + Gap + TabWidth * 0.5f, y, TabWidth, tabHeight);
            }

            float panelLeft = left + TabWidth + Gap * 3f;
            float panelWidth = size.x * 0.5f - Gap - panelLeft;
            Place(header.transform, header, panelLeft + panelWidth * 0.5f, top - HeaderHeight * 0.5f, panelWidth, HeaderHeight);
            float rowTop = top - HeaderHeight - Gap;
            foreach (var section in sections)
            {
                float y = rowTop - RowHeight * 0.5f;
                foreach (var row in section.Rows)
                {
                    lastWidth = panelWidth;
                    row.Layout(y, panelLeft, panelWidth);
                    float extra = row.Height != null ? Mathf.Max(0f, row.Height(panelWidth) - RowHeight) : 0f;
                    y -= RowHeight + extra + Gap;
                }
            }
        }
    }
}
