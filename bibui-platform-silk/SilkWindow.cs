using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
// Silk's key enum, for asking its keyboard what is held. What reaches the
// runtime is Bibui.Core.Key / Bibui.Core.MouseButton: the same values, by a cast.
using SilkKey = Silk.NET.Input.Key;

namespace Bibui.Core.Runtime.Platform;

/// <summary>
/// A window on GLFW through Silk.NET: what <see cref="Open"/> makes, and
/// <see cref="WaitEvents"/> waits on between frames.
/// It knows nothing of any renderer or graphics API: <see cref="DefaultOptions"/>
/// opens it with none, and the route that draws into it asks for its own in the
/// options. <see cref="SilkGl"/> is the OpenGL route: its options give the window
/// a GL context, and <see cref="SilkGl.Target"/> is the framebuffer a renderer
/// draws into, between <see cref="SilkGl.BeginDraw"/> and <see cref="SilkGl.EndDraw"/>.
/// The clipboard goes through TextCopy (wl-clipboard on Wayland).
///
/// <para>One thread: the one that opened it runs every frame.</para>
/// <code>
/// using var window = SilkWindow.Open(SilkGl.Options(new("My app", 960, 640)));
/// runtime.WakeUp = window.Wake;
/// runtime.WindowClipboard = (window.GetClipboardText, window.SetClipboardText);
/// var gl = SilkGl.Target(window);
/// using var skia = new SkiaGlRenderer(gl);
/// while (!window.IsClosing)
/// {
///     bool idle = runtime.FramesRequested == 0 && window.Events.Count == 0;
///     if (!idle) runtime.SleepUntilNextFrame();
///     var events = window.GetEvents(wait: idle);
///     if (runtime.FramesRequested == 0 && events.Count == 0) continue;
///     runtime.ConsumeFrame();
///     runtime.BeginBuild(window.Frame, events);
///     // ... build ...
///     runtime.EndBuild();
///     SilkGl.BeginDraw(window, gl);
///     skia.Draw(runtime.DrawList);
///     SilkGl.EndDraw(window);
/// }
/// </code>
/// </summary>
public sealed class SilkWindow : IDisposable
{
    public readonly IWindow Native;

    /// <summary>
    /// What the keyboard and mouse did since the last frame, in order: GLFW's
    /// callbacks append here and do nothing else, so no runtime call runs inside
    /// GLFW's own dispatch. The loop hands it to the runtime
    /// (<c>runtime.BeginBuild(window.Frame, window.Events)</c>), which empties it.
    /// </summary>
    public readonly List<WindowEvent> Events = [];

    internal SilkWindow(IWindow native)
    {
        Native = native;
    }

    /// <summary>
    /// The app's frame, called from GLFW's resize callback, so the window is drawn
    /// at its new size, and keeps drawing while GLFW holds the thread in its own
    /// pump (a drag on Windows or macOS). Without it a resize draws nothing until
    /// the next input.
    /// </summary>
    public Action? OnRefresh { get; set; }

    /// <summary>
    /// The window's <c>NSWindow</c> on macOS, for a route that draws into it
    /// without GL (<c>MetalLayer.Attach</c>); 0 elsewhere.
    /// </summary>
    public nint CocoaHandle => Native.Native?.Cocoa ?? 0;

    /// <summary>Framebuffer size in physical px.</summary>
    public int FramebufferWidth { get; private set; }

    /// <inheritdoc cref="FramebufferWidth"/>
    public int FramebufferHeight { get; private set; }

    /// <summary>Physical px per logical px.</summary>
    public float Scale { get; internal set; } = 1f;

    /// <summary>True once the user or <see cref="Close"/> has asked the window to close.</summary>
    public bool IsClosing => Native.IsClosing;

    /// <summary>Asks the window to close: <see cref="IsClosing"/> turns true.</summary>
    public void Close() => Native.Close();

    /// <summary>Makes a waiting <see cref="WaitEvents"/> return. Safe from any thread.</summary>
    public void Wake() => Native.ContinueEvents();

    /// <summary>
    /// The refresh rate of the monitor the window is on, in Hz, as that monitor
    /// runs now: read every time it is asked, so a screen switched to another
    /// rate has it on the next frame. 60 when GLFW knows no monitor. Which
    /// monitor is looked up when the window opens, when it moves, and when a
    /// monitor is connected or unplugged; on Wayland GLFW reports no moves and
    /// cannot tell which monitor a window is on, so with several monitors this
    /// may be another's.
    /// </summary>
    public float RefreshRate => _monitor?.VideoMode.RefreshRate is int hz && hz > 0 ? hz : 60f;

    // The monitor the window is on. Silk's lookup (Native.Monitor) works it out
    // from the window's position and allocates, so it runs when that can change,
    // not every frame.
    private IMonitor? _monitor;

    /// <summary>
    /// False: <see cref="GetEvents"/> takes what is queued and returns, so the
    /// loop paces itself (<c>runtime.SleepUntilNextFrame</c>).
    /// </summary>
    public bool PacesFrames => false;

    /// <summary>This frame's window, for <c>runtime.BeginBuild(window.Frame, window.Events)</c>.</summary>
    public WindowFrame Frame => new(FramebufferWidth, FramebufferHeight, Scale, RefreshRate);

    /// <summary>The system clipboard's text, or null when it holds none (TextCopy; wl-clipboard on Wayland).</summary>
    public string? GetClipboardText() => Clipboard.GetText();

    /// <summary>Puts <paramref name="text"/> on the system clipboard.</summary>
    public void SetClipboardText(string text) => Clipboard.SetText(text);

    // ── Input ────────────────────────────────────────────────────────────────

    // TODO: wired in Open, before the first frame. On Wayland the device
    // enumeration does blocking xkbcommon keymap reads and libinput probing that
    // can take 300+ ms, enough to miss several xdg_wm_base pings and fire
    // Hyprland's not-responding dialog. Find out why it blocks and fix it there.
    private IKeyboard? _keyboard;
    private IMouse? _mouse;
    private CursorShape _cursor;
    // Held so the GC keeps it while GLFW holds its function pointer.
    private global::Silk.NET.GLFW.GlfwCallbacks.CharCallback? _onChar;
    private global::Silk.NET.GLFW.GlfwCallbacks.KeyCallback? _onKey;

    private unsafe void WireInput()
    {
        var input = Native.CreateInput();
        var events = Events;
        foreach (var mouse in input.Mice)
        {
            mouse.MouseDown += (m, b) => events.Add(WindowEvent.Mouse(WindowEventKind.MouseDown, m.Position.X, m.Position.Y, (Bibui.Core.MouseButton)b, HeldModifiers));
            mouse.MouseUp   += (m, b) => events.Add(WindowEvent.Mouse(WindowEventKind.MouseUp,   m.Position.X, m.Position.Y, (Bibui.Core.MouseButton)b, HeldModifiers));
            mouse.MouseMove += (_, p) => events.Add(WindowEvent.Mouse(WindowEventKind.MouseMove, p.X, p.Y, Bibui.Core.MouseButton.Unknown, HeldModifiers));
            mouse.Scroll    += (m, w) => events.Add(WindowEvent.Wheel(m.Position.X, m.Position.Y, w.X, w.Y, HeldModifiers));
        }
        // Text straight from GLFW, not Silk.NET's KeyChar: that casts GLFW's code
        // point to a char, which turns 😀 (U+1F600) into U+F600. Taking the window's
        // char callback replaces Silk.NET's, whose only listener was KeyChar.
        _onChar = (_, codePoint) =>
        {
            if (System.Text.Rune.TryCreate(codePoint, out var r)) events.Add(WindowEvent.Text(r));
        };
        global::Silk.NET.GLFW.GlfwProvider.GLFW.Value.SetCharCallback(
            (global::Silk.NET.GLFW.WindowHandle*)Native.Native!.Glfw!.Value, _onChar);
        // Keys straight from GLFW too: Silk.NET's KeyDown drops GLFW's repeats, so
        // a held Backspace deleted one character. bibui's Key values are GLFW's
        // key codes, so a key converts with a cast. Taking the key callback
        // replaces Silk.NET's, whose only listeners were KeyDown and KeyUp.
        _onKey = (_, key, _, action, _) => events.Add(action switch
        {
            global::Silk.NET.GLFW.InputAction.Press  => WindowEvent.KeyPress((Bibui.Core.Key)key, HeldModifiers),
            global::Silk.NET.GLFW.InputAction.Repeat => WindowEvent.KeyRepeat((Bibui.Core.Key)key, HeldModifiers),
            _                                        => WindowEvent.KeyRelease((Bibui.Core.Key)key, HeldModifiers),
        });
        global::Silk.NET.GLFW.GlfwProvider.GLFW.Value.SetKeyCallback(
            (global::Silk.NET.GLFW.WindowHandle*)Native.Native!.Glfw!.Value, _onKey);
        _keyboard = input.Keyboards.FirstOrDefault();
        _mouse = input.Mice.FirstOrDefault();
        if (_cursor != CursorShape.Default) ApplyCursor();
    }

    // The modifiers held now, asked of the keyboard as each event arrives and
    // stamped on it: GLFW knows them, though Silk.NET's events do not carry them.
    private KeyModifiers HeldModifiers
    {
        get
        {
            var kb = _keyboard;
            if (kb is null) return KeyModifiers.None;
            var m = KeyModifiers.None;
            if (kb.IsKeyPressed(SilkKey.ShiftLeft)   || kb.IsKeyPressed(SilkKey.ShiftRight))   m |= KeyModifiers.Shift;
            if (kb.IsKeyPressed(SilkKey.ControlLeft) || kb.IsKeyPressed(SilkKey.ControlRight)) m |= KeyModifiers.Ctrl;
            if (kb.IsKeyPressed(SilkKey.AltLeft)     || kb.IsKeyPressed(SilkKey.AltRight))     m |= KeyModifiers.Alt;
            if (kb.IsKeyPressed(SilkKey.SuperLeft)   || kb.IsKeyPressed(SilkKey.SuperRight))   m |= KeyModifiers.Super;
            return m;
        }
    }

    /// <summary>Shows <paramref name="shape"/> while the pointer is over the window: GLFW's standard cursors.</summary>
    public void SetCursor(CursorShape shape)
    {
        _cursor = shape;
        if (_mouse is not null) ApplyCursor();
    }

    private void ApplyCursor()
    {
        var cursor = _mouse!.Cursor;
        var standard = _cursor switch
        {
            CursorShape.Pointer          => StandardCursor.Hand,
            CursorShape.Text             => StandardCursor.IBeam,
            CursorShape.Crosshair        => StandardCursor.Crosshair,
            CursorShape.ResizeHorizontal => StandardCursor.HResize,
            CursorShape.ResizeVertical   => StandardCursor.VResize,
            CursorShape.ResizeAll        => StandardCursor.ResizeAll,
            CursorShape.NotAllowed       => StandardCursor.NotAllowed,
            CursorShape.Wait             => StandardCursor.Wait,
            _                            => StandardCursor.Default,
        };
        if (!cursor.IsSupported(standard)) standard = StandardCursor.Default;
        cursor.Type = CursorType.Standard;
        cursor.StandardCursor = standard;
    }

    /// <summary>Looks up which monitor the window is on, for <see cref="RefreshRate"/>.</summary>
    private void FindMonitor() => _monitor = Native.Monitor;

    // Held so the GC keeps it while GLFW holds its function pointer.
    private global::Silk.NET.GLFW.GlfwCallbacks.MonitorCallback? _onMonitor;

    /// <summary>
    /// Closes the window and destroys it with any graphics context it was opened
    /// with. Dispose a renderer drawing into it first: its GPU context goes before
    /// the window's.
    /// </summary>
    public void Dispose()
    {
        Native.Reset();
        Native.Dispose();
    }

    // ── The module: open, then bracket every frame ──────────────────────────

    private static readonly Lock _glfwLock = new();
    private static string? _bundledGlfw;
    /// <summary>
    /// Loads the GLFW that NuGet shipped (Ultz.Native.GLFW), by the path it
    /// shipped it to, and returns that path; null when there is none to load.
    /// <see cref="Open"/> calls it; so may anything that creates a Silk.NET
    /// window of its own, such as a hidden GPU context.
    ///
    /// Silk.NET 2.23 tries the bare name first (the system loader path), then
    /// runtimes/&lt;rid&gt;/native under the app — but it takes that RID from
    /// /etc/os-release (Microsoft.DotNet.PlatformAbstractions), not from the
    /// runtime, and falls back to linux-&lt;arch&gt; only for a fixed list of
    /// distro IDs. Omarchy's os-release says ID=omarchy, so Silk looks in
    /// runtimes/omarchy.4.0.4-x64/native and nowhere else. With no libglfw in
    /// /usr/lib the load fails, IsApplicable goes false, and Window.Create
    /// reports "Couldn't find a suitable window platform", which reads as a
    /// broken display rather than a missing file. The runtime's own RID is no
    /// help either: Arch's source-built .NET reports arch-x64, and NuGet packs
    /// natives under the portable linux-x64.
    ///
    /// So this looks where the build actually put it: beside the app (a
    /// RID-specific publish), under the runtime's RID, then under the portable
    /// linux-&lt;arch&gt;. Loading it by absolute path registers it under its
    /// SONAME, libglfw.so.3, so Silk's dlopen of that name finds it resident.
    /// Best effort by design: if no bundled copy loads, nothing changes and
    /// Silk.NET searches as before.
    /// </summary>
    public static string? LoadBundledGlfw()
    {
        if (!OperatingSystem.IsLinux()) return null;
        lock (_glfwLock)
        {
            if (_bundledGlfw is not null) return _bundledGlfw;

            string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            string[] candidates =
            [
                Path.Combine(AppContext.BaseDirectory, "libglfw.so.3"),
                Path.Combine(AppContext.BaseDirectory, "runtimes",
                    System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "native", "libglfw.so.3"),
                Path.Combine(AppContext.BaseDirectory, "runtimes", $"linux-{arch}", "native", "libglfw.so.3"),
            ];
            foreach (var path in candidates)
            {
                if (File.Exists(path) && System.Runtime.InteropServices.NativeLibrary.TryLoad(path, out _))
                    return _bundledGlfw = path;
            }
            return null;
        }
    }

    /// <summary>
    /// The options every bibui window opens with: <paramref name="options"/>'s
    /// title and size, resizable, and no graphics API. A route that draws with
    /// one asks for it on top of these (<see cref="SilkGl.Options"/>). Keep
    /// IsEventDriven, FramesPerSecond and UpdatesPerSecond: Silk.NET's own loop
    /// must not run, as <see cref="WaitEvents"/> waits on GLFW itself.
    /// </summary>
    public static WindowOptions DefaultOptions(PlatformWindowOptions options) => WindowOptions.Default with
    {
        Title = options.Title,
        Size = new Vector2D<int>(options.Width, options.Height),
        WindowBorder = WindowBorder.Resizable,
        API = GraphicsAPI.None,
        // VSync off: on Wayland the compositor stops sending frame callbacks
        // to an occluded window, and a vsynced SwapBuffers then blocks,
        // starving the event pump until Hyprland's not-responding dialog.
        VSync = false,
        // No frames of Silk.NET's own; ContinueEvents wakes WaitEvents from any thread.
        IsEventDriven = true,
        FramesPerSecond = 0.0,
        UpdatesPerSecond = 0.0,
    };

    /// <summary>
    /// Opens a window with <paramref name="windowOptions"/> (<see cref="DefaultOptions"/>,
    /// or a route's on top of them), with its input wired. It knows no runtime:
    /// the loop hands the runtime <see cref="Frame"/> and <see cref="Events"/>, and
    /// wires <see cref="Wake"/> and the clipboard to it. Set
    /// <see cref="SilkWindow.OnRefresh"/> to the loop body to keep it drawing
    /// during a resize on Windows or macOS.
    /// </summary>
    public static SilkWindow Open(WindowOptions windowOptions)
    {
        LoadBundledGlfw();
        // Silk.NET finds platforms by reflection, which Native AOT trims away.
        global::Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
        global::Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();

        var native = global::Silk.NET.Windowing.Window.Create(windowOptions);
        var window = new SilkWindow(native);
        native.Load += () =>
        {
            window.Resize(native.FramebufferSize);
            window.FindMonitor();
        };
        // Moved onto another monitor (X11; Wayland reports no moves).
        native.Move += _ => window.FindMonitor();
        native.FramebufferResize += size =>
        {
            window.Resize(size);
            // Draw at the new size from here: GLFW is in its own pump during a drag
            // on Windows and macOS, and the app's loop is not running; and nothing
            // else asks for a frame on a resize.
            window.OnRefresh?.Invoke();
        };
        native.FocusChanged += focused => { if (!focused) window.Events.Add(WindowEvent.FocusLost()); };
        native.Initialize();
        window.WireInput();
        // A monitor connected or unplugged: look the monitor up again, as an
        // unplugged one is freed under the IMonitor that names it. GLFW has one
        // monitor callback for the process, so the one set before is called on.
        // Unsafe only for the callback's type: the monitor pointer is never read.
        unsafe
        {
            global::Silk.NET.GLFW.GlfwCallbacks.MonitorCallback? previous = null;
            window._onMonitor = (monitor, state) =>
            {
                window.FindMonitor();
                previous?.Invoke(monitor, state);
            };
            previous = global::Silk.NET.GLFW.GlfwProvider.GLFW.Value.SetMonitorCallback(window._onMonitor);
        }

        return window;
    }

    private void Resize(Vector2D<int> framebuffer)
    {
        var size = Native.Size;
        FramebufferWidth = framebuffer.X;
        FramebufferHeight = framebuffer.Y;
        Scale = size.X > 0 ? (float)framebuffer.X / size.X : 1f;
    }

    /// <summary>
    /// Takes what the OS has queued into <see cref="Events"/> and returns them.
    /// With <paramref name="wait"/>, for when nothing else is happening, it first
    /// waits until something does: input, a <see cref="Wake"/> (from any thread),
    /// or the window closing. Without, it takes what came while the loop slept.
    /// The loop's pacing is the runtime's.
    /// </summary>
    public List<WindowEvent> GetEvents(bool wait)
    {
        var glfw = global::Silk.NET.GLFW.GlfwProvider.GLFW.Value;
        if (wait) glfw.WaitEvents();
        else glfw.PollEvents();
        return Events;
    }
}
