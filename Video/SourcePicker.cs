using Windows.Graphics.Capture;

namespace Jerboa.Video;

/// <summary>
/// Choosing what to record. Windows draws the dialog, which is why it says "share"
/// rather than "record" — in exchange it shows live previews of every window and screen,
/// handles the permission, and is the one piece of this feature nobody has to maintain.
/// </summary>
public static class SourcePicker
{
    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    /// <summary>Returns the chosen window or screen, or null when the dialog was dismissed.</summary>
    public static async Task<GraphicsCaptureItem?> ChooseAsync(IntPtr owner)
    {
        try
        {
            var picker = new GraphicsCapturePicker();
            Interop.SetOwner(picker, owner);
            return await picker.PickSingleItemAsync();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a source picked earlier can still be recorded. A window that has since
    /// been closed reports a size of nothing, which is the cheapest way to find out
    /// without starting a capture session.
    /// </summary>
    public static bool StillUsable(GraphicsCaptureItem? item)
    {
        if (item == null) return false;
        try { return item.Size.Width > 0 && item.Size.Height > 0; }
        catch { return false; }
    }
}
