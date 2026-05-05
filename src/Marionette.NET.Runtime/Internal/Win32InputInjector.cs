// Marionette.NET — Phase 14 Win32 SendInput injector
//
// Universal Windows input-injection path that bypasses framework-specific
// quirks. The OS turns a kernel-level synthetic input into the proper
// framework events (WPF KeyEventArgs, WinUI KeyRoutedEventArgs, MAUI handler
// events) — we don't have to construct any of them ourselves.
//
// Why we ship this:
//   * WinUI's InputInjector requires Win11 22000+ and an interactive session;
//     SendInput works on Win7+ and in non-elevated processes.
//   * MAUI on the Windows head has no public key_down / key_up API at all.
//   * WPF's InputManager.PostProcessInput path occasionally fails when a
//     control's IsKeyboardFocusWithin invariant doesn't match — SendInput
//     side-steps this entirely.
//   * Same code-path for all three adapters → one test surface, one bug fix
//     site.
//
// AOT contract:
//   * Pure P/Invoke; no reflection, no MakeGeneric*, no Type.GetType.
//   * The `INPUT` / `MOUSEINPUT` / `KEYBDINPUT` structs are plain blittable
//     structs — Native AOT marshals them directly.
//   * Windows-only — call sites guard with RuntimeInformation.IsOSPlatform.
//
// Threading:
//   * SendInput is thread-safe. Caller does NOT need to be on the UI thread.
//   * Input is delivered to whatever window currently has focus. For
//     Marionette's typical use, the caller arranges focus first (semantic
//     focus shift, then SendInput).

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Marionette.Runtime.Internal;

/// <summary>
/// Phase 14: Win32 <c>SendInput</c>-based input injector. Used by all three
/// Windows-targeting adapters (WPF, WinUI, MAUI Windows head) as a universal
/// fallback when framework-specific input paths don't apply.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Win32InputInjector
{
    /// <summary>
    /// True when the running OS is Windows. Call sites should guard with
    /// this property before invoking the Send* methods — calling on
    /// non-Windows throws <see cref="PlatformNotSupportedException"/>.
    /// </summary>
    public static bool IsAvailable => OperatingSystem.IsWindows();

    /// <summary>
    /// Send a synthetic key-down event for <paramref name="key"/>. The OS
    /// turns this into the appropriate framework key event on the focused
    /// window (WPF KeyDown, WinUI KeyDown, MAUI handler key event).
    /// </summary>
    public static bool SendKeyDown(VirtualKey key)
    {
        var inputs = new INPUT[1];
        inputs[0] = MakeKeyboardInput((ushort)key, dwFlags: 0);
        return SendInputs(inputs);
    }

    /// <summary>Send a synthetic key-up event for <paramref name="key"/>.</summary>
    public static bool SendKeyUp(VirtualKey key)
    {
        var inputs = new INPUT[1];
        inputs[0] = MakeKeyboardInput((ushort)key, dwFlags: KEYEVENTF_KEYUP);
        return SendInputs(inputs);
    }

    /// <summary>
    /// Send a complete key press (down then up) for <paramref name="key"/>.
    /// Common case: function keys, Enter, Tab, Escape, arrow keys.
    /// </summary>
    public static bool SendKeyPress(VirtualKey key)
    {
        var inputs = new INPUT[2];
        inputs[0] = MakeKeyboardInput((ushort)key, dwFlags: 0);
        inputs[1] = MakeKeyboardInput((ushort)key, dwFlags: KEYEVENTF_KEYUP);
        return SendInputs(inputs);
    }

    /// <summary>
    /// Type a Unicode string by emitting per-character key events with the
    /// <see cref="KEYEVENTF_UNICODE"/> flag. Bypasses keyboard layout
    /// translation — the codepoint goes through verbatim, which is what
    /// adopters want for non-ASCII input (umlauts, emoji, CJK).
    /// </summary>
    public static bool SendUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        // Two INPUT events per char (down + up). Use a stackalloc-friendly
        // size cap — most type_text payloads are short. Above that we
        // allocate; SendInput accepts any reasonable batch size.
        var inputs = new INPUT[text.Length * 2];
        int idx = 0;
        foreach (var ch in text)
        {
            inputs[idx++] = MakeKeyboardInput(0, dwFlags: KEYEVENTF_UNICODE, scanCode: ch);
            inputs[idx++] = MakeKeyboardInput(0, dwFlags: KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, scanCode: ch);
        }
        return SendInputs(inputs);
    }

    /// <summary>
    /// Move the mouse to absolute screen coordinates. Coordinates are in
    /// the OS's virtual-desktop space (use 0..65535 for normalised
    /// absolute coords; we accept pixel coords and normalise them).
    /// </summary>
    public static bool SendMouseMoveAbsolute(int screenX, int screenY)
    {
        // Normalise to 0..65535 across the primary monitor. For multi-monitor
        // we'd OR-in MOUSEEVENTF_VIRTUALDESK — simple primary path here keeps
        // the contract obvious; multi-monitor adopters can pre-translate.
        var (sw, sh) = GetPrimaryScreenSize();
        var nx = (int)((screenX * 65535L) / Math.Max(1, sw));
        var ny = (int)((screenY * 65535L) / Math.Max(1, sh));
        var inputs = new INPUT[1];
        inputs[0] = MakeMouseInput(
            dx: nx,
            dy: ny,
            dwFlags: MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE);
        return SendInputs(inputs);
    }

    /// <summary>
    /// Send a complete mouse-button click (down + up) at the current cursor
    /// position. For a click on a specific screen point, call
    /// <see cref="SendMouseMoveAbsolute"/> first.
    /// </summary>
    public static bool SendMouseClick(MouseButton button)
    {
        var (downFlag, upFlag) = button switch
        {
            MouseButton.Left => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
            MouseButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (0u, 0u),
        };
        if (downFlag == 0) return false;
        var inputs = new INPUT[2];
        inputs[0] = MakeMouseInput(0, 0, dwFlags: downFlag);
        inputs[1] = MakeMouseInput(0, 0, dwFlags: upFlag);
        return SendInputs(inputs);
    }

    /// <summary>
    /// Map a Marionette key-name (e.g. "Enter", "Escape", "F1", "A") to a
    /// virtual-key code. Returns <see langword="null"/> when the name
    /// isn't recognised — callers fall back to per-char Unicode typing.
    /// </summary>
    public static VirtualKey? TryParseKeyName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        // Fast path: single ASCII letter or digit → VK code.
        if (name!.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c >= 'A' && c <= 'Z') return (VirtualKey)c;
            if (c >= '0' && c <= '9') return (VirtualKey)c;
        }
        return name switch
        {
            "Enter" or "Return" => VirtualKey.Enter,
            "Escape" or "Esc" => VirtualKey.Escape,
            "Tab" => VirtualKey.Tab,
            "Backspace" => VirtualKey.Backspace,
            "Delete" or "Del" => VirtualKey.Delete,
            "Space" => VirtualKey.Space,
            "Up" or "ArrowUp" => VirtualKey.Up,
            "Down" or "ArrowDown" => VirtualKey.Down,
            "Left" or "ArrowLeft" => VirtualKey.Left,
            "Right" or "ArrowRight" => VirtualKey.Right,
            "Home" => VirtualKey.Home,
            "End" => VirtualKey.End,
            "PageUp" => VirtualKey.PageUp,
            "PageDown" => VirtualKey.PageDown,
            "Insert" or "Ins" => VirtualKey.Insert,
            "F1" => VirtualKey.F1,
            "F2" => VirtualKey.F2,
            "F3" => VirtualKey.F3,
            "F4" => VirtualKey.F4,
            "F5" => VirtualKey.F5,
            "F6" => VirtualKey.F6,
            "F7" => VirtualKey.F7,
            "F8" => VirtualKey.F8,
            "F9" => VirtualKey.F9,
            "F10" => VirtualKey.F10,
            "F11" => VirtualKey.F11,
            "F12" => VirtualKey.F12,
            "Shift" => VirtualKey.Shift,
            "Ctrl" or "Control" => VirtualKey.Control,
            "Alt" or "Menu" => VirtualKey.Alt,
            _ => null,
        };
    }

    // -------------------------------------------------------------------------
    // P/Invoke surface
    // -------------------------------------------------------------------------

    private static bool SendInputs(INPUT[] inputs)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Win32InputInjector requires Windows.");
        if (inputs.Length == 0) return true;
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return sent == inputs.Length;
    }

    private static INPUT MakeKeyboardInput(ushort vk, uint dwFlags, ushort scanCode = 0)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.U.ki = new KEYBDINPUT
        {
            wVk = vk,
            wScan = scanCode,
            dwFlags = dwFlags,
            time = 0,
            dwExtraInfo = IntPtr.Zero,
        };
        return input;
    }

    private static INPUT MakeMouseInput(int dx, int dy, uint dwFlags, int mouseData = 0)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.U.mi = new MOUSEINPUT
        {
            dx = dx,
            dy = dy,
            mouseData = mouseData,
            dwFlags = dwFlags,
            time = 0,
            dwExtraInfo = IntPtr.Zero,
        };
        return input;
    }

    private static (int Width, int Height) GetPrimaryScreenSize()
    {
        if (!OperatingSystem.IsWindows()) return (1920, 1080);
        var w = GetSystemMetrics(SM_CXSCREEN);
        var h = GetSystemMetrics(SM_CYSCREEN);
        return (w, h);
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}

/// <summary>
/// Phase 14: Windows virtual-key codes for the keys Marionette commonly
/// handles. Adopters who need additional codes can pass the raw ushort
/// directly to <see cref="Win32InputInjector"/> overloads via cast.
/// </summary>
public enum VirtualKey : ushort
{
    Backspace = 0x08,
    Tab = 0x09,
    Enter = 0x0D,
    Shift = 0x10,
    Control = 0x11,
    Alt = 0x12,
    Escape = 0x1B,
    Space = 0x20,
    PageUp = 0x21,
    PageDown = 0x22,
    End = 0x23,
    Home = 0x24,
    Left = 0x25,
    Up = 0x26,
    Right = 0x27,
    Down = 0x28,
    Insert = 0x2D,
    Delete = 0x2E,
    F1 = 0x70,
    F2 = 0x71,
    F3 = 0x72,
    F4 = 0x73,
    F5 = 0x74,
    F6 = 0x75,
    F7 = 0x76,
    F8 = 0x77,
    F9 = 0x78,
    F10 = 0x79,
    F11 = 0x7A,
    F12 = 0x7B,
}

/// <summary>
/// Phase 14: mouse buttons recognised by <see cref="Win32InputInjector.SendMouseClick"/>.
/// </summary>
public enum MouseButton
{
    Left,
    Right,
    Middle,
}
