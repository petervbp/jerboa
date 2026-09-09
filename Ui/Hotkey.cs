using System.Runtime.InteropServices;

namespace Jerboa.Ui;

/// <summary>
/// A system-wide shortcut. Windows hands it to exactly one application, so registration
/// can fail when something else already holds the combination — the caller is told rather
/// than left with a key that silently does nothing.
/// </summary>
public sealed class Hotkey : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x4A45;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    private bool _registered;

    public event Action? Pressed;

    public Hotkey() => CreateHandle(new CreateParams());

    /// <summary>Returns false when the combination is already taken by another application.</summary>
    public bool Register(bool control, bool shift, bool alt, Keys key)
    {
        Unregister();

        uint modifiers = ModNoRepeat;
        if (control) modifiers |= ModControl;
        if (shift) modifiers |= ModShift;
        if (alt) modifiers |= ModAlt;

        _registered = RegisterHotKey(Handle, HotkeyId, modifiers, (uint)key);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(Handle, HotkeyId);
        _registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId) Pressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }
}

/// <summary>A field that records the next key combination pressed into it.</summary>
public sealed class ShortcutBox : TextBox
{
    public event Action<bool, bool, bool, Keys>? Captured;

    public ShortcutBox()
    {
        ReadOnly = true;
        Cursor = Cursors.Hand;
        TextAlign = HorizontalAlignment.Center;
    }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;

        var key = e.KeyCode;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;
        if (!e.Control && !e.Alt) return;   // a global shortcut without Ctrl or Alt would swallow ordinary typing

        Captured?.Invoke(e.Control, e.Shift, e.Alt, key);
    }
}
