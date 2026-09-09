using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Jerboa.Audio;

namespace Jerboa.Ui;

/// <summary>
/// The icons Jerboa shows. The window and the taskbar always carry the artwork; the
/// notification area carries the artwork while idle, orange bars while paused, and a
/// slowly pulsing red dot while recording, so a glance at the corner of the screen
/// tells you whether anything is being written.
/// </summary>
public sealed class TrayIcons : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private const int Canvas = 32;
    private const int PulseFrames = 12;

    /// <summary>How long one breath of the recording dot takes.</summary>
    public static readonly TimeSpan PulsePeriod = TimeSpan.FromMilliseconds(1600);

    private static readonly Color RecordingRed = Color.FromArgb(226, 75, 74);
    private static readonly Color PausedAmber = Color.FromArgb(239, 159, 39);

    private readonly List<IntPtr> _handles = new();
    private readonly Icon[] _pulse = new Icon[PulseFrames];

    /// <summary>The artwork, at window and taskbar size.</summary>
    public Icon App { get; }

    /// <summary>The artwork, at notification-area size.</summary>
    public Icon Idle { get; }

    public Icon Paused { get; }

    public TrayIcons()
    {
        App = LoadArtwork(SystemInformation.IconSize.Width);
        Idle = LoadArtwork(SystemInformation.SmallIconSize.Width);
        Paused = Build(DrawPauseBars);

        for (int i = 0; i < PulseFrames; i++)
        {
            // A cosine breath rather than a blink — noticeable in the corner of the eye
            // without demanding attention.
            double breath = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / PulseFrames);
            _pulse[i] = Build(g => DrawDot(g, breath));
        }
    }

    /// <summary>The recording icon for a given point in time.</summary>
    public Icon RecordingAt(TimeSpan elapsed) => _pulse[FrameIndex(elapsed)];

    public static int FrameIndex(TimeSpan elapsed) =>
        (int)(elapsed.TotalMilliseconds / PulsePeriod.TotalMilliseconds * PulseFrames) % PulseFrames;

    public Icon For(SessionState state, TimeSpan elapsed) => state switch
    {
        SessionState.Recording => RecordingAt(elapsed),
        SessionState.Paused => Paused,
        _ => Idle
    };

    private static Icon LoadArtwork(int size)
    {
        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream("Jerboa.ico")
                           ?? throw new InvalidOperationException("The application icon is missing from the build.");
        return new Icon(stream, size, size);
    }

    private Icon Build(Action<Graphics> draw)
    {
        using var bitmap = new Bitmap(Canvas, Canvas);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            draw(g);
        }

        var handle = bitmap.GetHicon();
        _handles.Add(handle);
        return (Icon)Icon.FromHandle(handle).Clone();
    }

    private static void DrawDot(Graphics g, double breath)
    {
        float radius = (float)(10.5 + 3.5 * breath);
        int alpha = (int)(170 + 85 * breath);

        using var brush = new SolidBrush(Color.FromArgb(alpha, RecordingRed));
        float centre = Canvas / 2f;
        g.FillEllipse(brush, centre - radius, centre - radius, radius * 2, radius * 2);
    }

    private static void DrawPauseBars(Graphics g)
    {
        using var brush = new SolidBrush(PausedAmber);
        using var left = Rounded(new Rectangle(7, 4, 7, 24), 2);
        using var right = Rounded(new Rectangle(18, 4, 7, 24), 2);
        g.FillPath(brush, left);
        g.FillPath(brush, right);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        App.Dispose();
        Idle.Dispose();
        Paused.Dispose();
        foreach (var icon in _pulse) icon.Dispose();
        foreach (var handle in _handles) DestroyIcon(handle);
        _handles.Clear();
    }
}
