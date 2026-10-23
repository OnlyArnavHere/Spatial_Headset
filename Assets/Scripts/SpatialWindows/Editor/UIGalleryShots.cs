using System.Collections.Generic;
using System.Text;

namespace SpatialLauncher.SpatialWindows.Editor
{
    public enum GalleryState { Idle, Hover, Pressed, Focused, Disabled }

    /// <summary>One screenshot of the UI gallery: a window in a state, saved as <see cref="FileName"/>.</summary>
    public readonly struct GalleryShot
    {
        public readonly string Window;
        public readonly GalleryState State;

        public GalleryShot(string window, GalleryState state)
        {
            Window = window;
            State = state;
        }

        public string FileName => UIGalleryShots.FileName(Window, State);
    }

    /// <summary>
    /// The pure logic of the UI gallery (<see cref="UIGallery"/>): which windows and states are shot, what the files are
    /// called and how a label becomes a folder name. No Unity objects, so it can be tested without a scene.
    /// </summary>
    static class UIGalleryShots
    {
        public const string Overview = "overview";
        public const string DefaultLabel = "latest";

        /// <summary>Window ids, in shot order. Each is built by <see cref="UIGallery"/>.</summary>
        public static readonly string[] Windows =
        {
            "home", "image", "video", "remote_stream", "laptop_picker", "pairing_code", "settings", "consent", "keyboard",
            "shell_windows", "shell_macos", "shell_search", "shell_locked", "shell_app_windows",
        };

        /// <summary>
        /// Controls have a disabled look (<see cref="WindowControl.Interactable"/>), so the Disabled shots join the list.
        /// </summary>
        public const bool DisabledSupported = true;

        /// <summary>
        /// Whether a window has controls to hover and press. The image and the video placeholder are display-only, and an
        /// unlinked remote stream has no panel.
        /// </summary>
        public static bool HasControls(string window)
        {
            switch (window)
            {
                case "image":
                case "video":
                case "remote_stream":
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>Whether a window has a disabled look to shoot (once <see cref="DisabledSupported"/>): the ones with buttons.</summary>
        public static bool HasDisabled(string window) => HasControls(window);

        /// <summary>The states shot for a window, in order.</summary>
        public static List<GalleryState> StatesFor(string window, bool includeDisabled = DisabledSupported)
        {
            var states = new List<GalleryState> { GalleryState.Idle };
            if (HasControls(window))
            {
                states.Add(GalleryState.Hover);
                states.Add(GalleryState.Pressed);
            }
            states.Add(GalleryState.Focused);
            if (includeDisabled && HasDisabled(window)) states.Add(GalleryState.Disabled);
            return states;
        }

        /// <summary>Every window shot, window by window (without the overview).</summary>
        public static List<GalleryShot> All(bool includeDisabled = DisabledSupported)
        {
            var shots = new List<GalleryShot>();
            foreach (var window in Windows)
                foreach (var state in StatesFor(window, includeDisabled))
                    shots.Add(new GalleryShot(window, state));
            return shots;
        }

        /// <summary>The file for a window in a state: <c>home_hover.png</c>.</summary>
        public static string FileName(string window, GalleryState state) => window + "_" + state.ToString().ToLowerInvariant() + ".png";

        public static string OverviewFileName => Overview + ".png";

        /// <summary>
        /// A label as a folder name: letters, digits, '-' and '_' stay, everything else (spaces, dots, slashes, so no path
        /// tricks) becomes '_'. Blank gives <see cref="DefaultLabel"/>.
        /// </summary>
        public static string SanitizeLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return DefaultLabel;
            var text = new StringBuilder(label.Trim().Length);
            foreach (char c in label.Trim())
                text.Append(c < 128 && (char.IsLetterOrDigit(c) || c == '-' || c == '_') ? c : '_');
            return text.ToString();
        }
    }
}
