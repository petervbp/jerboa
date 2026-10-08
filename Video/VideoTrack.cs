using Windows.Graphics.Capture;

namespace Jerboa.Video;

/// <summary>
/// The video half of a recording: a capture source and the encoder behind it, bundled so
/// the recording session only has to start, pause and stop one thing.
///
/// It is deliberately self-contained and failure-tolerant. If the window disappears or the
/// encoder dies, the video simply ends there — the audio recording knows nothing about it
/// and carries on.
/// </summary>
public sealed class VideoTrack : IDisposable
{
    private readonly WindowCapture _capture = new();
    private readonly VideoWriter _writer;
    private readonly GraphicsCaptureItem _item;
    private bool _ended;

    /// <summary>Raised when the captured window closes mid-recording.</summary>
    public event Action<string>? Ended;

    public VideoTrack(GraphicsCaptureItem item, string path)
    {
        _item = item;
        SourceName = string.IsNullOrWhiteSpace(item.DisplayName) ? "the selected source" : item.DisplayName;
        _writer = new VideoWriter(path, item.Size.Width, item.Size.Height);
    }

    public string SourceName { get; }
    public string Path => _writer.Path;
    public int SourceWidth => _item.Size.Width;
    public int SourceHeight => _item.Size.Height;
    public long FramesWritten => _writer.FramesWritten;
    public DateTime? FirstFrameUtc => _writer.FirstFrameUtc;

    /// <summary>False while the source has never produced a picture — a minimised window, typically.</summary>
    public bool SawPicture => _writer.FramesAccepted > 0;

    public string? Failure => _writer.Failure;

    public void Start()
    {
        _capture.MinimumInterval = TimeSpan.FromSeconds(0.9 / VideoWriter.FramesPerSecond);
        _capture.FrameReceived = (in CapturedFrame frame) => _writer.Accept(frame);
        _capture.Closed += OnSourceClosed;

        _writer.Start();
        _capture.Start(_item);
    }

    private void OnSourceClosed()
    {
        if (_ended) return;
        _ended = true;
        try { _capture.Stop(); } catch { }
        try { _writer.Stop(); } catch { }
        Ended?.Invoke($"Video stopped - {SourceName} was closed. Audio is still recording.");
    }

    public void Pause() => _writer.Pause();

    public void Resume()
    {
        if (!_ended) _writer.Resume();
    }

    public void Stop()
    {
        if (_ended) return;
        _ended = true;
        _capture.Stop();
        _writer.Stop();
    }

    public void Dispose()
    {
        Stop();
        _capture.Dispose();
        _writer.Dispose();
    }
}
