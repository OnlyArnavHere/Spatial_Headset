using SpatialLauncher.SpatialWindows.Theme;
using UnityEngine;

namespace SpatialLauncher.LaptopShell
{
    /// <summary>How the app bar marks a running app: a short dash under the icon, or a small dot.</summary>
    public enum RunningIndicator { Dash, Dot }

    /// <summary>
    /// The Laptop Shell window's look for the laptop's OS, drawn with our own shapes, Inter and Material Icons (no OS
    /// logos, fonts or icon sets; the app icons themselves come from the laptop).
    /// Windows: a full-width taskbar with the icons centred and a dash under running apps (a longer accent one for the
    /// app in front), and a Start-style panel: a rounded search box over Pinned, with All apps a tap away.
    /// macOS: a floating glass dock sized to its icons, with a dot under running apps and a divider before the running
    /// apps that aren't pinned, and a large capsule search over the Applications grid.
    /// Other OSes get the Windows layout (the shell isn't offered there yet).
    /// </summary>
    public sealed class ShellStyle
    {
        public ShellOs Os { get; private set; }
        /// <summary>The bar spans the content width (taskbar) or fits its icons (dock).</summary>
        public bool BarFullWidth { get; private set; }
        public float BarRadiusPt { get; private set; }
        public float BarHeight { get; private set; }
        /// <summary>Visible app icon in the bar (the hit area is the whole slot).</summary>
        public float BarIcon { get; private set; }
        /// <summary>Width of one bar slot (at least the 44 mm hit area).</summary>
        public float BarSlot { get; private set; }
        public RunningIndicator Indicator { get; private set; }
        /// <summary>A divider between pinned apps and running apps that aren't pinned.</summary>
        public bool DividerBeforeUnpinned { get; private set; }
        /// <summary>A first bar button that shows the app panel (taskbar style).</summary>
        public bool AppsButtonInBar { get; private set; }
        /// <summary>Search: a rounded box (radius) or a capsule.</summary>
        public bool SearchCapsule { get; private set; }
        public float SearchRadiusPt { get; private set; }
        public float SearchHeight { get; private set; }
        /// <summary>Fraction of the content width the search takes (centred).</summary>
        public float SearchWidth { get; private set; }
        public TextRole SearchText { get; private set; }
        public string SearchPlaceholder { get; private set; }
        /// <summary>The grid shows pins first with a toggle to all apps (Start), or every app (Applications).</summary>
        public bool PinnedFirst { get; private set; }
        public string GridTitle { get; private set; }
        public string AllAppsTitle { get; private set; }
        public string TopResultTitle { get; private set; }
        public int GridColumns { get; private set; }
        public int GridRows { get; private set; }
        public float GridIcon { get; private set; }

        public static ShellStyle For(ShellOs os)
        {
            if (os == ShellOs.MacOS)
                return new ShellStyle
                {
                    Os = os, BarFullWidth = false, BarRadiusPt = 20f, BarHeight = 0.074f, BarIcon = 0.044f, BarSlot = 0.060f,
                    Indicator = RunningIndicator.Dot, DividerBeforeUnpinned = true, AppsButtonInBar = false,
                    SearchCapsule = true, SearchRadiusPt = 0f, SearchHeight = 0.054f, SearchWidth = 0.78f, SearchText = TextRole.Title,
                    SearchPlaceholder = "Search", PinnedFirst = false, GridTitle = "Applications", AllAppsTitle = "Applications",
                    TopResultTitle = "Top hit", GridColumns = 7, GridRows = 3, GridIcon = 0.048f,
                };
            return new ShellStyle
            {
                Os = os, BarFullWidth = true, BarRadiusPt = 8f, BarHeight = 0.064f, BarIcon = 0.036f, BarSlot = 0.054f,
                Indicator = RunningIndicator.Dash, DividerBeforeUnpinned = false, AppsButtonInBar = true,
                SearchCapsule = false, SearchRadiusPt = 8f, SearchHeight = 0.048f, SearchWidth = 1f, SearchText = TextRole.Body,
                SearchPlaceholder = "Type here to search apps and windows", PinnedFirst = true, GridTitle = "Pinned",
                AllAppsTitle = "All apps", TopResultTitle = "Best match", GridColumns = 6, GridRows = 3, GridIcon = 0.044f,
            };
        }

        /// <summary>Indicator size in metres for a running app; the app in front gets the longer accent dash (taskbar).</summary>
        public Vector2 IndicatorSize(bool focused) => Indicator == RunningIndicator.Dot
            ? new Vector2(0.005f, 0.005f)
            : new Vector2(focused ? 0.018f : 0.010f, 0.003f);

        public Color IndicatorColor(bool focused) => Indicator == RunningIndicator.Dash && focused
            ? UITheme.Accent
            : new Color(1f, 1f, 1f, 0.75f);
    }
}
