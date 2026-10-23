using System;
using System.Collections.Generic;
using SpatialLauncher.SpatialWindows;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.Switcher
{
    /// <summary>
    /// The switcher's overview: a card per window (headset windows first, most recent first, then the laptop's windows
    /// not streamed yet), the selected one with the focus ring. Pinching or clicking a card picks it; the page follows
    /// the selection. Cards show the laptop's own icon for laptop windows and a glyph for headset windows.
    /// </summary>
    public class SwitcherContent : WindowContent
    {
        public static readonly Vector2 PreferredSize = new Vector2(0.66f, 0.44f);
        public static Vector2 WindowSize => PreferredSize + Vector2.one * (Padding * 2f);
        public const int Columns = 4, Rows = 3, PerPage = Columns * Rows;
        const float Gap = 0.008f, HeaderHeight = 0.046f, HintHeight = 0.03f, Icon = 0.044f, RoundButton = 0.044f;
        static readonly int UseTexId = Shader.PropertyToID("_UseTex");
        static readonly int TexAlphaId = Shader.PropertyToID("_TexAlpha");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        public sealed class Card
        {
            public WindowButton Button;
            public Transform IconQuad;
            public Material IconMaterial;
            public TextMeshPro Glyph, Title, Detail;
            public SwitcherEntry Entry;
            public int Index;
        }

        public event Action<int> Picked;
        public IReadOnlyList<Card> Cards => shown;
        public int Page { get; private set; }
        public int PageCount { get; private set; } = 1;
        public int Selected { get; private set; } = -1;
        public string HintText => hint != null ? hint.text : "";
        public string HeaderText => header != null ? header.text : "";
        public WindowButton PreviousButton { get; private set; }
        public WindowButton NextButton { get; private set; }

        readonly List<Card> pool = new List<Card>();
        readonly List<Card> shown = new List<Card>();
        IReadOnlyList<SwitcherEntry> entries = Array.Empty<SwitcherEntry>();
        TextMeshPro header, hint, pageLabel, previousGlyph, nextGlyph, empty;

        public void Init(SpatialWindow window, Material baseMaterial)
        {
            Attach(window, baseMaterial);
            header = CreateLabel("Header", transform, "Switch to", TextRole.Headline, TextAlignmentOptions.MidlineLeft);
            hint = CreateLabel("Hint", transform, "", TextRole.Secondary, TextAlignmentOptions.Center);
            empty = CreateLabel("Empty", transform, "No other windows are open.", TextRole.Body, TextAlignmentOptions.Center);
            pageLabel = CreateLabel("PageLabel", transform, "", TextRole.Caption, TextAlignmentOptions.Center);
            PreviousButton = CreateControl<WindowButton>("PreviousPage", transform, CreateRounded(UITheme.GlassRaised, 0f));
            Round(PreviousButton.transform, 0f, capsule: true);
            previousGlyph = Glyph("PreviousGlyph", UIIcons.ChevronLeft);
            PreviousButton.BindLabel(previousGlyph);
            PreviousButton.Clicked += () => ShowPage(Page - 1);
            NextButton = CreateControl<WindowButton>("NextPage", transform, CreateRounded(UITheme.GlassRaised, 0f));
            Round(NextButton.transform, 0f, capsule: true);
            nextGlyph = Glyph("NextGlyph", UIIcons.ChevronRight);
            NextButton.BindLabel(nextGlyph);
            NextButton.Clicked += () => ShowPage(Page + 1);
            for (int i = 0; i < PerPage; i++)
            {
                var card = new Card { Button = CreateControl<WindowButton>($"Card {i}", transform, CreateRounded(UITheme.GlassRaised, UITheme.ControlRadius)) };
                card.IconMaterial = CreateRounded(Color.white, 0f);
                card.IconQuad = CreateQuad($"CardIcon {i}", transform, card.IconMaterial, FillLayer);
                card.Glyph = Glyph($"CardGlyph {i}", UIIcons.Apps);
                card.Title = CreateLabel($"CardTitle {i}", transform, "", TextRole.Body, TextAlignmentOptions.Center);
                card.Title.richText = false;
                card.Detail = CreateLabel($"CardDetail {i}", transform, "", TextRole.Caption, TextAlignmentOptions.Center);
                card.Detail.richText = false;
                card.Button.BindLabel(card.Title);
                card.Button.BindLabel(card.Detail);
                var bound = card;
                card.Button.Clicked += () => { if (bound.Entry != null) Picked?.Invoke(bound.Index); };
                pool.Add(card);
            }
            Show(entries, -1, "");
        }

        TextMeshPro Glyph(string glyphName, string icon)
        {
            var glyph = CreateLabel(glyphName, transform, icon, TextRole.Icon, TextAlignmentOptions.Center);
            glyph.overflowMode = TextOverflowModes.Overflow;
            return glyph;
        }

        /// <summary>Shows these entries with <paramref name="selected"/> marked, and a hint line.</summary>
        public void Show(IReadOnlyList<SwitcherEntry> list, int selected, string hintText)
        {
            entries = list ?? Array.Empty<SwitcherEntry>();
            hint.text = hintText ?? "";
            PageCount = Mathf.Max(1, Mathf.CeilToInt(entries.Count / (float)PerPage));
            Selected = selected;
            Page = selected >= 0 ? Mathf.Clamp(selected / PerPage, 0, PageCount - 1) : Mathf.Clamp(Page, 0, PageCount - 1);
            Bind();
        }

        /// <summary>Moves the focus ring (and the page) to an entry.</summary>
        public void Select(int index)
        {
            Selected = index;
            if (index >= 0) Page = Mathf.Clamp(index / PerPage, 0, PageCount - 1);
            Bind();
        }

        /// <summary>The laptop icons arrived: draw them.</summary>
        public void RefreshIcons() => Bind();

        void ShowPage(int page)
        {
            Page = Mathf.Clamp(page, 0, PageCount - 1);
            Bind();
        }

        void Bind()
        {
            shown.Clear();
            for (int i = 0; i < pool.Count; i++)
            {
                var card = pool[i];
                int index = Page * PerPage + i;
                bool used = index < entries.Count;
                foreach (var part in new Component[] { card.Button, card.IconQuad, card.Glyph, card.Title, card.Detail })
                    part.gameObject.SetActive(used);
                card.Entry = used ? entries[index] : null;
                card.Index = index;
                card.Button.SetFocused(used && index == Selected);
                if (!used) continue;
                var entry = card.Entry;
                card.Title.text = entry.Title;
                card.Detail.text = entry.Detail;
                var texture = entry.IconId != null && entry.Client != null ? entry.Client.Icon(entry.IconId) : null;
                if (texture != null)
                {
                    card.IconMaterial.mainTexture = texture;
                    card.IconMaterial.SetFloat(UseTexId, 1f);
                    card.IconMaterial.SetFloat(TexAlphaId, 1f);
                    card.IconMaterial.SetColor(ColorId, Color.white);
                    card.Glyph.gameObject.SetActive(false);
                }
                else
                {
                    card.IconMaterial.SetFloat(UseTexId, 0f);
                    card.IconMaterial.SetFloat(TexAlphaId, 0f);
                    card.IconMaterial.SetColor(ColorId, new Color(1f, 1f, 1f, 0f));
                    card.Glyph.text = entry.Glyph;
                }
                shown.Add(card);
            }
            header.text = Selected >= 0 && Selected < entries.Count ? $"Switch to: {entries[Selected].Title}" : "Switch to";
            empty.gameObject.SetActive(entries.Count == 0);
            bool pages = PageCount > 1;
            foreach (var part in new Component[] { PreviousButton, NextButton, previousGlyph, nextGlyph, pageLabel })
                part.gameObject.SetActive(pages);
            pageLabel.text = $"{Page + 1} / {PageCount}";
            PreviousButton.Interactable = Page > 0;
            NextButton.Interactable = Page < PageCount - 1;
            Relayout();
        }

        protected override void Layout(Vector2 size)
        {
            if (header == null) return;
            float top = size.y * 0.5f, bottom = -size.y * 0.5f, left = -size.x * 0.5f, right = size.x * 0.5f;
            float headerY = top - HeaderHeight * 0.5f;
            Text(header, left + size.x * 0.3f, headerY, size.x * 0.6f, HeaderHeight);
            float nextX = right - RoundButton * 0.5f, pageX = nextX - RoundButton * 0.5f - 0.03f, previousX = pageX - 0.03f - RoundButton * 0.5f;
            Quad(NextButton.transform, nextX, headerY, -0.002f, RoundButton, RoundButton);
            Text(nextGlyph, nextX, headerY, RoundButton, RoundButton, -0.004f);
            Quad(PreviousButton.transform, previousX, headerY, -0.002f, RoundButton, RoundButton);
            Text(previousGlyph, previousX, headerY, RoundButton, RoundButton, -0.004f);
            Text(pageLabel, pageX, headerY, 0.06f, HeaderHeight);

            float hintY = bottom + HintHeight * 0.5f;
            Text(hint, 0f, hintY, size.x, HintHeight);
            float gridTop = top - HeaderHeight - Gap, gridBottom = hintY + HintHeight * 0.5f + Gap;
            float cellWidth = size.x / Columns, cellHeight = (gridTop - gridBottom) / Rows;
            Text(empty, 0f, (gridTop + gridBottom) * 0.5f, size.x, 0.05f);
            for (int i = 0; i < pool.Count; i++)
            {
                var card = pool[i];
                int row = i / Columns, column = i % Columns;
                float cx = left + cellWidth * (column + 0.5f), cy = gridTop - cellHeight * (row + 0.5f);
                Quad(card.Button.transform, cx, cy, -0.001f, cellWidth - Gap, cellHeight - Gap);
                float iconY = cy + cellHeight * 0.18f;
                Quad(card.IconQuad, cx, iconY, -0.002f, Icon, Icon);
                Text(card.Glyph, cx, iconY, Icon, Icon, -0.004f);
                Text(card.Title, cx, cy - cellHeight * 0.12f, cellWidth - Gap * 3f, 0.024f);
                Text(card.Detail, cx, cy - cellHeight * 0.31f, cellWidth - Gap * 3f, 0.02f);
            }
        }

        static void Quad(Transform quad, float x, float y, float z, float width, float height)
        {
            quad.localPosition = new Vector3(x, y, z);
            quad.localScale = new Vector3(width, height, 1f);
        }

        static void Text(TextMeshPro text, float x, float y, float width, float height, float z = -0.003f)
        {
            text.transform.localPosition = new Vector3(x, y, z);
            text.rectTransform.sizeDelta = new Vector2(width, height);
        }
    }
}
