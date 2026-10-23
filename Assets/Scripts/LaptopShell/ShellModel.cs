using System.Collections.Generic;

namespace SpatialLauncher.LaptopShell
{
    /// <summary>Which laptop the shell talks to; the Laptop Shell window styles itself after it.</summary>
    public enum ShellOs { Unknown, Windows, MacOS, Linux }

    /// <summary>One laptop window as the agent listed it. <see cref="Id"/> is "window:&lt;handle&gt;", the same id a stream opens.</summary>
    public sealed class ShellWindow
    {
        public string Id;
        public string Title;
        public bool Minimized;
        public bool Focused;
    }

    /// <summary>
    /// One laptop app. <see cref="Id"/> is the agent's opaque "app:&lt;16 hex&gt;" id for this session; the same app has the
    /// same id in the running, pinned, installed and search lists.
    /// </summary>
    public sealed class ShellApp
    {
        public string Id;
        public string Name;
        public bool Pinned;
        public bool Running;
        /// <summary>The agent can start it (false for programs it only saw running).</summary>
        public bool Launchable = true;
        public readonly List<ShellWindow> Windows = new List<ShellWindow>();
    }

    public enum ShellResultKind { App, Window }

    /// <summary>One search hit: an app (open or launch it) or an open window (stream it).</summary>
    public sealed class ShellResult
    {
        public ShellResultKind Kind;
        public string Id;
        public string Name;
        /// <summary>For a window: the app it belongs to.</summary>
        public string AppName;
        public bool Running;
    }
}
