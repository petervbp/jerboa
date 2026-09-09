using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Jerboa.Audio;

/// <summary>
/// One WASAPI input — microphone or loopback — reduced to a single mono stream at the
/// session sample rate. Whatever the device hands over (float, 16/24/32-bit, any channel
/// count) leaves here as one normalised channel ready to be placed left or right.
/// </summary>
public sealed class CaptureSource : IDisposable
{
    private readonly WasapiCapture _capture;
    private readonly SampleQueue _queue;
    private readonly WaveFormat _deviceFormat;
    private float[] _scratch = new float[8192];
    private bool _stopRequested;

    public ISampleProvider Output { get; }
    public string Label { get; }

    /// <summary>Peak level of the most recent buffer, 0 to 1.</summary>
    public float Peak { get; private set; }

    /// <summary>Raised when the device stops on its own — usually because it was unplugged.</summary>
    public event Action<string>? Lost;

    public CaptureSource(WasapiCapture capture, int targetSampleRate, string label)
    {
        _capture = capture;
        _deviceFormat = capture.WaveFormat;
        Label = label;
        _queue = new SampleQueue(_deviceFormat.SampleRate);

        Output = _deviceFormat.SampleRate == targetSampleRate
            ? _queue
            : new WdlResamplingSampleProvider(_queue, targetSampleRate);

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    public long DroppedSamples => _queue.DroppedSamples;
    public long PaddedSamples => _queue.PaddedSamples;

    public void Start() => _capture.StartRecording();

    public void Stop()
    {
        _stopRequested = true;
        try { _capture.StopRecording(); } catch { /* already gone */ }
    }

    /// <summary>Throws away everything buffered — used while paused so nothing piles up.</summary>
    public void Discard()
    {
        _queue.Clear();
        Peak = 0f;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int samples = Decode(e.Buffer, e.BytesRecorded);
        if (samples == 0) return;

        int channels = _deviceFormat.Channels;
        int frames = samples / channels;
        float peak = 0f;

        // Fold every channel of the device into one, in place.
        for (int f = 0; f < frames; f++)
        {
            float sum = 0f;
            int src = f * channels;
            for (int c = 0; c < channels; c++) sum += _scratch[src + c];

            float value = sum / channels;
            _scratch[f] = value;

            float magnitude = Math.Abs(value);
            if (magnitude > peak) peak = magnitude;
        }

        Peak = peak;
        _queue.Write(_scratch, frames);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopRequested) return;
        Lost?.Invoke(e.Exception?.Message ?? $"{Label} stopped unexpectedly.");
    }

    /// <summary>Converts the device's native sample format into normalised floats.</summary>
    private int Decode(byte[] buffer, int bytes)
    {
        int bits = _deviceFormat.BitsPerSample;
        int bytesPerSample = bits / 8;
        if (bytesPerSample == 0) return 0;

        int count = bytes / bytesPerSample;
        if (_scratch.Length < count) _scratch = new float[count];

        bool isFloat = _deviceFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (_deviceFormat.Encoding == WaveFormatEncoding.Extensible && bits == 32);

        if (isFloat)
        {
            for (int i = 0; i < count; i++) _scratch[i] = BitConverter.ToSingle(buffer, i * 4);
        }
        else if (bits == 16)
        {
            for (int i = 0; i < count; i++) _scratch[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
        }
        else if (bits == 24)
        {
            for (int i = 0; i < count; i++)
            {
                int o = i * 3;
                int value = buffer[o] | (buffer[o + 1] << 8) | ((sbyte)buffer[o + 2] << 16);
                _scratch[i] = value / 8388608f;
            }
        }
        else if (bits == 32)
        {
            for (int i = 0; i < count; i++) _scratch[i] = BitConverter.ToInt32(buffer, i * 4) / 2147483648f;
        }
        else return 0;

        return count;
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
    }
}
