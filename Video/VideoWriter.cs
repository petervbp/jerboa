using System.Diagnostics;

namespace Jerboa.Video;

/// <summary>
/// Writes captured frames out as video at a steady rate.
///
/// The capture API only produces a frame when the window's content changes, so a slide
/// left on screen yields nothing for minutes. Writing those frames as they arrive would
/// put the video on a timeline of its own and slide it against the audio. Instead this
/// keeps the most recent frame and writes it on its own cadence, taken from the wall
/// clock — the same approach the audio side already uses, which is what lets the two be
/// laid against each other at the end.
/// </summary>
public sealed class VideoWriter : IDisposable
{
    public const int FramesPerSecond = 8;
    public const int OutputHeight = 1080;

    private readonly object _gate = new();
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly int _stride;

    private byte[] _latest;
    private byte[] _outgoing;
    private bool _hasFrame;

    private Process? _ffmpeg;
    private Stream? _input;
    private Thread? _pump;
    private volatile bool _running;
    private volatile bool _paused;

    public string Path { get; }
    public long FramesWritten { get; private set; }
    public long FramesAccepted { get; private set; }

    /// <summary>When the first frame was written, for lining the video up against the audio.</summary>
    public DateTime? FirstFrameUtc { get; private set; }

    /// <summary>Set when the encoder died; the recording carries on without video.</summary>
    public string? Failure { get; private set; }

    public VideoWriter(string path, int sourceWidth, int sourceHeight)
    {
        Path = path;
        _sourceWidth = Math.Max(2, sourceWidth);
        _sourceHeight = Math.Max(2, sourceHeight);
        _stride = _sourceWidth * 4;

        _latest = new byte[_stride * _sourceHeight];
        _outgoing = new byte[_latest.Length];
    }

    public void Start()
    {
        // The raw stream has to keep one size for its whole length, so the source size at
        // this moment is the size we are committed to. ffmpeg scales it to 1080p on the
        // way into the encoder — a window resized mid-recording is fitted into the frame
        // we started with rather than changing the stream.
        var arguments =
            $"-hide_banner -loglevel error -y " +
            $"-f rawvideo -pixel_format bgra -video_size {_sourceWidth}x{_sourceHeight} " +
            $"-framerate {FramesPerSecond} -i - -an " +
            $"-vf scale=-2:{OutputHeight}:flags=bicubic " +
            $"-c:v libx264 -preset veryfast -crf 28 -pix_fmt yuv420p " +
            $"-movflags +faststart \"{Path}\"";

        _ffmpeg = Ffmpeg.StartWithPipedInput(arguments);
        _input = _ffmpeg.StandardInput.BaseStream;

        // Start from black rather than from nothing. A minimised window delivers no frames
        // at all, and without a seed the pump would write nothing and leave an unplayable
        // file behind. With it the timeline always exists, and FramesAccepted tells the
        // caller whether any real picture ever turned up.
        _hasFrame = true;

        _running = true;
        _pump = new Thread(Pump) { IsBackground = true, Name = "Jerboa video pump" };
        _pump.Start();
    }

    /// <summary>
    /// Takes a frame from the capture thread. Rows are copied tightly packed; a frame that
    /// no longer matches the size we committed to is cropped or padded rather than scaled,
    /// because that only happens when someone resizes the window mid-recording.
    /// </summary>
    public void Accept(in CapturedFrame frame)
    {
        lock (_gate)
        {
            int rows = Math.Min(frame.Height, _sourceHeight);
            int bytesPerRow = Math.Min(frame.Width, _sourceWidth) * 4;

            if (frame.Width < _sourceWidth || frame.Height < _sourceHeight)
                Array.Clear(_latest);

            for (int y = 0; y < rows; y++)
            {
                var source = frame.Pixels.Slice(y * frame.Stride, bytesPerRow);
                source.CopyTo(_latest.AsSpan(y * _stride, bytesPerRow));
            }

            _hasFrame = true;
            FramesAccepted++;
        }
    }

    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    private void Pump()
    {
        var clock = Stopwatch.StartNew();
        var previous = clock.Elapsed;
        var recorded = TimeSpan.Zero;

        while (_running)
        {
            Thread.Sleep(10);

            var now = clock.Elapsed;
            var delta = now - previous;
            previous = now;

            if (_paused || !_hasFrame) continue;
            recorded += delta;

            long wanted = (long)(recorded.TotalSeconds * FramesPerSecond);
            int due = (int)Math.Min(wanted - FramesWritten, FramesPerSecond * 2);
            if (due <= 0) continue;

            // One copy under the lock, then the pipe write happens outside it — a write
            // can take milliseconds and must not hold up the capture thread.
            lock (_gate) Array.Copy(_latest, _outgoing, _latest.Length);

            for (int i = 0; i < due && _running; i++)
            {
                try
                {
                    _input!.Write(_outgoing, 0, _outgoing.Length);
                    FirstFrameUtc ??= DateTime.UtcNow;
                    FramesWritten++;
                }
                catch (Exception ex)
                {
                    Failure = "The video encoder stopped: " + ex.Message;
                    _running = false;
                    break;
                }
            }
        }
    }

    /// <summary>Stops writing and waits for the encoder to close the file properly.</summary>
    public void Stop()
    {
        _running = false;
        _pump?.Join(2000);

        try { _input?.Flush(); _input?.Close(); } catch { }

        try
        {
            if (_ffmpeg != null && !_ffmpeg.WaitForExit(15000))
            {
                _ffmpeg.Kill();
                Failure ??= "The video encoder did not finish in time.";
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_running) Stop();
        _ffmpeg?.Dispose();
        _ffmpeg = null;
    }
}
