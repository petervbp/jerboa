using System.Runtime.InteropServices;

namespace Jerboa.Ui;

/// <summary>
/// Keeps one Jerboa per session. A second launch does not open a second recorder —
/// it asks the one already running to show its window and then gets out of the way.
/// </summary>
public static class SingleInstance
{
    private const int HwndBroadcast = 0xFFFF;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>The broadcast that means "you are already running, come to the front".</summary>
    public static readonly uint ShowWindowMessage = RegisterWindowMessage("JerboaShowWindow");

    private static Mutex? _mutex;

    /// <summary>True when this process is the first one; false when another already holds the name.</summary>
    public static bool Claim()
    {
        _mutex = new Mutex(initiallyOwned: true, @"Local\Jerboa.SingleInstance", out bool first);
        if (!first)
        {
            _mutex.Dispose();
            _mutex = null;
        }
        return first;
    }

    public static void AskRunningInstanceToShow() =>
        PostMessage(HwndBroadcast, ShowWindowMessage, IntPtr.Zero, IntPtr.Zero);

    public static void Release()
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _mutex = null;
    }
}
