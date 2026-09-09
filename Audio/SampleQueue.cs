using NAudio.Wave;

namespace Jerboa.Audio;

/// <summary>
/// Thread-safe ring buffer between a capture callback and the recording pump.
/// Reads always return the requested number of samples: a device that has fallen
/// behind yields silence rather than a short read, which is what keeps the two
/// channels aligned to the same timeline.
/// </summary>
public sealed class SampleQueue : ISampleProvider
{
    private readonly object _gate = new();
    private readonly float[] _buffer;
    private readonly int _dropThreshold;
    private readonly int _dropTarget;
    private int _read;
    private int _count;

    public WaveFormat WaveFormat { get; }

    /// <summary>Samples discarded because the device produced faster than the clock consumed.</summary>
    public long DroppedSamples { get; private set; }

    /// <summary>Silence inserted because the device produced slower than the clock consumed.</summary>
    public long PaddedSamples { get; private set; }

    public SampleQueue(int sampleRate, double capacitySeconds = 4.0)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        _buffer = new float[Math.Max(1024, (int)(sampleRate * capacitySeconds))];
        _dropThreshold = (int)(sampleRate * 0.5);
        _dropTarget = (int)(sampleRate * 0.1);
    }

    public void Write(float[] source, int count)
    {
        lock (_gate)
        {
            for (int i = 0; i < count; i++)
            {
                if (_count == _buffer.Length) { Advance(1); DroppedSamples++; }
                _buffer[(_read + _count) % _buffer.Length] = source[i];
                _count++;
            }

            // The capture clock runs slightly fast against the wall clock; trim the
            // backlog before it turns into audible latency between the two channels.
            if (_count > _dropThreshold)
            {
                int excess = _count - _dropTarget;
                Advance(excess);
                DroppedSamples += excess;
            }
        }
    }

    public int Read(float[] destination, int offset, int count)
    {
        lock (_gate)
        {
            int available = Math.Min(count, _count);
            for (int i = 0; i < available; i++)
                destination[offset + i] = _buffer[(_read + i) % _buffer.Length];
            Advance(available);

            if (available < count)
            {
                Array.Clear(destination, offset + available, count - available);
                PaddedSamples += count - available;
            }
        }
        return count;
    }

    public void Clear()
    {
        lock (_gate) { _read = 0; _count = 0; }
    }

    private void Advance(int samples)
    {
        _read = (_read + samples) % _buffer.Length;
        _count -= samples;
    }
}
