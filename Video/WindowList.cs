using System.Runtime.InteropServices;
using System.Text;

namespace Jerboa.Video;

/// <summary>Finding windows and monitors by hand — used by the diagnostic capture test.</summary>
internal static class WindowList
{
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    private const uint MonitorDefaultToPrimary = 1;

    public static IntPtr PrimaryMonitor() => MonitorFromWindow(IntPtr.Zero, MonitorDefaultToPrimary);

    /// <summary>The first visible top-level window whose title contains the given text.</summary>
    public static (IntPtr Handle, string Title) FindByTitle(string substring)
    {
        IntPtr found = IntPtr.Zero;
        string foundTitle = "";

        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;

            var buffer = new StringBuilder(512);
            if (GetWindowTextW(window, buffer, buffer.Capacity) == 0) return true;

            var title = buffer.ToString();
            if (title.Length == 0) return true;
            if (title.IndexOf(substring, StringComparison.OrdinalIgnoreCase) < 0) return true;

            found = window;
            foundTitle = title;
            return false;
        }, IntPtr.Zero);

        return (found, foundTitle);
    }
}
