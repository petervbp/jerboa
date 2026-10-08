using System.Diagnostics;
using System.Globalization;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;

namespace Jerboa.Audio;

public enum SessionState { Idle, Recording, Paused }

public sealed record SessionResult(string FilePath, TimeSpan Duration, string? Problem = null);

/// <summary>
/// A single recording. Microphone and system audio are captured independently and
/// written into one stereo MP3 — microphone left, system audio right — on a timeline
/// driven by the wall clock, so neither device's own clock can pull the two apart.
/// </summary>
public sealed class RecordingSession : IDisposable
{
    public const int SampleRate = 48000;
    private const string ChannelNote = "Left: microphone / Right: system audio";

    private readonly object _gate = new();

    private CaptureSource? _microphone;
    private CaptureSource? _system;
    private WasapiOut? _keepAlive;
    private LameMP3FileWriter? _writer;
    private Thread? _pump;

    private volatile bool _running;
    private volatile bool _paused;
    private long _framesWritten;
    private string _partPath = "";
    private string _videoPartPath = "";
    private string _baseName = "";
    private Video.VideoTrack? _video;
    private string _folder = "";

    private float[] _left = Array.Empty<float>();
    private float[] _right = Array.Empty<float>();
    private byte[] _pcm = Array.Empty<byte>();

    public SessionState State { get; private set; } = SessionState.Idle;
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Interlocked.Read(ref _framesWritten) / SampleRate);

    public float MicrophonePeak => _paused ? 0f : _microphone?.Peak ?? 0f;
    public float SystemPeak => _paused ? 0f : _system?.Peak ?? 0f;

    /// <summary>A device disappeared mid-recording. The session keeps whatever it can still hear.</summary>
    public event Action<string>? DeviceLost;

    /// <summary>A device could not be opened at all when starting.</summary>
    public event Action<string>? Warning;

    public void Start(MMDevice? renderDevice, MMDevice? captureDevice, string folder, DateTime startedAt,
                      Windows.Graphics.Capture.GraphicsCaptureItem? videoSource = null)
    {
        if (State != SessionState.Idle) throw new InvalidOperationException("A recording is already running.");

        Directory.CreateDirectory(folder);
        _folder = folder;
        _baseName = startedAt.ToString("yyyyMMdd HHmm") + "h";
        _partPath = Path.Combine(folder, _baseName + " Recording.part.mp3");
        _videoPartPath = Path.Combine(folder, _baseName + " Recording.part.mp4");
        _framesWritten = 0;
        _paused = false;

        OpenSystem(renderDevice);
        OpenMicrophone(captureDevice);

        if (_system == null && _microphone == null)
        {
            Cleanup();
            throw new InvalidOperationException("Neither the playback device nor the microphone could be opened.");
        }

        var config = new LameConfig
        {
            Mode = MPEGMode.Stereo,   // never joint stereo: the two channels are unrelated
            VBR = VBRMode.Default,
            VBRQuality = 5,
            ID3 = new ID3TagData
            {
                Title = _baseName + " Recording",
                Album = "Jerboa",
                Comment = ChannelNote
            }
        };

        _writer = new LameMP3FileWriter(_partPath, new WaveFormat(SampleRate, 16, 2), config);

        _running = true;
        State = SessionState.Recording;
        _pump = new Thread(Pump) { IsBackground = true, Name = "Jerboa pump", Priority = ThreadPriority.AboveNormal };
        _pump.Start();

        // Video comes last on purpose: the audio is the recording, and nothing about
        // starting the picture may delay or endanger it.
        if (videoSource != null) StartVideo(videoSource);
    }

    private void StartVideo(Windows.Graphics.Capture.GraphicsCaptureItem source)
    {
        try
        {
            _video = new Video.VideoTrack(source, _videoPartPath);
            _video.Ended += message => VideoEnded?.Invoke(message);
            _video.Start();
        }
        catch (Exception ex)
        {
            Warning?.Invoke("Video could not be started: " + ex.Message);
            _video?.Dispose();
            _video = null;
        }
    }

    private void OpenSystem(MMDevice? renderDevice)
    {
        if (renderDevice == null) return;
        try
        {
            var capture = new WasapiLoopbackCapture(renderDevice);
            _system = new CaptureSource(capture, SampleRate, "System audio");
            _system.Lost += message => DeviceLost?.Invoke(message);

            // A silent render stream keeps the endpoint awake; an idle device delivers
            // no loopback buffers at all, which would punch holes into the timeline.
            _keepAlive = new WasapiOut(renderDevice, AudioClientShareMode.Shared, false, 200);
            _keepAlive.Init(new SilenceProvider(capture.WaveFormat));
            _keepAlive.Play();

            _system.Start();
        }
        catch (Exception ex)
        {
            Warning?.Invoke("System audio could not be opened: " + ex.Message);
            _system?.Dispose();
            _system = null;
            _keepAlive?.Dispose();
            _keepAlive = null;
        }
    }

    private void OpenMicrophone(MMDevice? captureDevice)
    {
        if (captureDevice == null) return;
        try
        {
            _microphone = new CaptureSource(new WasapiCapture(captureDevice), SampleRate, "Microphone");
            _microphone.Lost += message => DeviceLost?.Invoke(message);
            _microphone.Start();
        }
        catch (Exception ex)
        {
            Warning?.Invoke("Microphone could not be opened: " + ex.Message);
            _microphone?.Dispose();
            _microphone = null;
        }
    }

    public void Pause()
    {
        if (State != SessionState.Recording) return;
        _paused = true;
        _video?.Pause();
        State = SessionState.Paused;
    }

    public void Resume()
    {
        if (State != SessionState.Paused) return;
        _paused = false;
        _video?.Resume();
        State = SessionState.Recording;
    }

    /// <summary>True while a recording is also capturing a picture.</summary>
    public bool HasVideo => _video != null;

    /// <summary>False while the chosen source has never produced a picture.</summary>
    public bool VideoSawPicture => _video?.SawPicture ?? false;

    /// <summary>The window or screen being captured.</summary>
    public string VideoSourceName => _video?.SourceName ?? "";

    /// <summary>Raised when the captured window closes while the recording continues.</summary>
    public event Action<string>? VideoEnded;

    /// <summary>Stops, closes the file and gives it its final name including the net duration.</summary>
    public SessionResult Stop()
    {
        if (State == SessionState.Idle) throw new InvalidOperationException("No recording is running.");

        _running = false;
        _pump?.Join(2000);

        _microphone?.Stop();
        _system?.Stop();
        try { _keepAlive?.Stop(); } catch { }

        var duration = Duration;
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }

        try { _video?.Stop(); } catch { }

        LastDriftReport = DriftReport();
        var result = Finalise(duration);

        _video?.Dispose();
        _video = null;
        Cleanup();
        State = SessionState.Idle;

        return result;
    }

    /// <summary>
    /// Gives the recording its final name, and lays the picture over the sound if there
    /// was one. Audio and video were written as two independent files on two independent
    /// pumps; both started from the same wall clock, so combining them is a single shift
    /// by the difference between their first written moments.
    /// </summary>
    private SessionResult Finalise(TimeSpan duration)
    {
        string length = DescribeLength(duration);
        bool hasVideo = _video != null && File.Exists(_videoPartPath) && new FileInfo(_videoPartPath).Length > 1024;

        if (!hasVideo)
        {
            TryDelete(_videoPartPath);
            return new SessionResult(MoveToFinal(_partPath, ".mp3", length), duration, _video?.Failure);
        }

        double offset = 0;
        if (_video!.FirstFrameUtc is { } videoStart && AudioStartUtc is { } audioStart)
            offset = (videoStart - audioStart).TotalSeconds;

        string target = UniquePath(".mp4", length);
        string videoShift = Shift(offset);
        string audioShift = Shift(-offset);

        var arguments =
            $"-hide_banner -loglevel error -y {videoShift}-i \"{_videoPartPath}\" {audioShift}-i \"{_partPath}\" " +
            $"-map 0:v -map 1:a -c copy -shortest \"{target}\"";

        if (Video.Ffmpeg.Run(arguments, out var diagnostics) && File.Exists(target))
        {
            TryDelete(_partPath);
            TryDelete(_videoPartPath);
            return new SessionResult(target, duration, _video.Failure);
        }

        // Nothing is thrown away because the two halves would not join: both are kept.
        var keptAudio = MoveToFinal(_partPath, ".mp3", length);
        MoveToFinal(_videoPartPath, ".mp4", length);
        return new SessionResult(keptAudio, duration,
            "Sound and picture could not be combined, so both were kept separately. " + FirstLine(diagnostics));
    }

    /// <summary>An ffmpeg input shift, for whichever of the two started later.</summary>
    private static string Shift(double seconds) =>
        seconds > 0.02 ? $"-itsoffset {seconds.ToString("0.###", CultureInfo.InvariantCulture)} " : "";

    private string UniquePath(string extension, string length)
    {
        string target = Path.Combine(_folder, $"{_baseName} Recording {length}{extension}");
        for (int suffix = 2; File.Exists(target); suffix++)
            target = Path.Combine(_folder, $"{_baseName} Recording {length} ({suffix}){extension}");
        return target;
    }

    private string MoveToFinal(string part, string extension, string length)
    {
        var target = UniquePath(extension, length);
        try { File.Move(part, target); return target; }
        catch { return part; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        return line.Trim();
    }

    /// <summary>
    /// The length as it appears in the file name, rounded to whole minutes and never
    /// below one, so a short recording reads "1 min" rather than "0 min".
    /// </summary>
    public static string DescribeLength(TimeSpan duration) =>
        $"{Math.Max(1, (int)Math.Round(duration.TotalMinutes))} min";

    /// <summary>
    /// Emits exactly as many frames as wall-clock time has passed, taking what each
    /// device has produced and padding with silence where one has fallen behind.
    /// </summary>
    /// <summary>When the audio timeline began, used to line the picture up against it.</summary>
    public DateTime? AudioStartUtc { get; private set; }

    private void Pump()
    {
        AudioStartUtc = DateTime.UtcNow;
        var clock = Stopwatch.StartNew();
        var previous = clock.Elapsed;
        var recorded = TimeSpan.Zero;

        while (_running)
        {
            Thread.Sleep(15);

            var now = clock.Elapsed;
            var delta = now - previous;
            previous = now;

            if (_paused)
            {
                _microphone?.Discard();
                _system?.Discard();
                continue;
            }

            recorded += delta;
            long wanted = (long)(recorded.TotalSeconds * SampleRate);
            int frames = (int)Math.Min(wanted - Interlocked.Read(ref _framesWritten), SampleRate);
            if (frames <= 0) continue;

            EnsureBuffers(frames);
            Fill(_microphone, _left, frames);
            Fill(_system, _right, frames);

            for (int i = 0; i < frames; i++)
            {
                short left = ToPcm16(_left[i]);
                short right = ToPcm16(_right[i]);
                int o = i * 4;
                _pcm[o] = (byte)left;
                _pcm[o + 1] = (byte)(left >> 8);
                _pcm[o + 2] = (byte)right;
                _pcm[o + 3] = (byte)(right >> 8);
            }

            lock (_gate)
            {
                if (_writer == null) break;
                try { _writer.Write(_pcm, 0, frames * 4); }
                catch { break; }
            }

            Interlocked.Add(ref _framesWritten, frames);
        }
    }

    private static void Fill(CaptureSource? source, float[] destination, int frames)
    {
        if (source == null) { Array.Clear(destination, 0, frames); return; }
        source.Output.Read(destination, 0, frames);
    }

    private void EnsureBuffers(int frames)
    {
        if (_left.Length < frames) _left = new float[frames];
        if (_right.Length < frames) _right = new float[frames];
        if (_pcm.Length < frames * 4) _pcm = new byte[frames * 4];
    }

    private static short ToPcm16(float value)
    {
        value = Math.Clamp(value, -1f, 1f);
        return (short)Math.Round(value * 32767f);
    }

    /// <summary>Alignment corrections from the session that just finished.</summary>
    public string LastDriftReport { get; private set; } = "";

    /// <summary>Alignment corrections applied so far — for the self-test.</summary>
    public string DriftReport()
    {
        string Describe(CaptureSource? s) => s == null
            ? "absent"
            : $"padded {s.PaddedSamples * 1000.0 / SampleRate:F0} ms, dropped {s.DroppedSamples * 1000.0 / SampleRate:F0} ms";
        return $"microphone: {Describe(_microphone)}; system: {Describe(_system)}";
    }

    private void Cleanup()
    {
        _microphone?.Dispose();
        _microphone = null;
        _system?.Dispose();
        _system = null;
        _keepAlive?.Dispose();
        _keepAlive = null;
    }

    public void Dispose()
    {
        if (State != SessionState.Idle)
        {
            try { Stop(); return; } catch { }
        }
        _running = false;
        lock (_gate) { _writer?.Dispose(); _writer = null; }
        Cleanup();
    }
}
