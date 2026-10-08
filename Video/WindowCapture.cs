using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Jerboa.Video;

/// <summary>
/// One captured frame, handed to the writer while it is still mapped. The pixels are
/// only valid for the duration of the call — copy what you need and return.
/// </summary>
public readonly ref struct CapturedFrame
{
    public CapturedFrame(ReadOnlySpan<byte> pixels, int width, int height, int stride, TimeSpan timestamp)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        Timestamp = timestamp;
    }

    /// <summary>Rows of BGRA, <see cref="Stride"/> bytes apart.</summary>
    public ReadOnlySpan<byte> Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }

    /// <summary>When the compositor produced the frame, on the same clock for every source.</summary>
    public TimeSpan Timestamp { get; }
}

public delegate void FrameHandler(in CapturedFrame frame);

/// <summary>
/// Captures one window or monitor through Windows.Graphics.Capture. Frames arrive only
/// when the content actually changes — a still slide produces nothing for minutes — so
/// whoever writes the video has to supply its own cadence.
/// </summary>
public sealed unsafe class WindowCapture : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _staging;
    private SizeInt32 _size;
    private bool _stopped;

    /// <summary>Raised when the captured window disappears.</summary>
    public event Action? Closed;

    public FrameHandler? FrameReceived;

    /// <summary>
    /// How close together two delivered frames may be. The compositor offers far more
    /// than the video needs, and reading a frame back out of video memory is the one
    /// genuinely expensive step — so frames that would be thrown away are skipped before
    /// that happens, not after.
    /// </summary>
    public TimeSpan MinimumInterval { get; set; } = TimeSpan.Zero;

    private readonly System.Diagnostics.Stopwatch _since = System.Diagnostics.Stopwatch.StartNew();
    private TimeSpan _lastDelivered = TimeSpan.FromHours(-1);

    public long FramesReceived { get; private set; }
    public long FramesSkipped { get; private set; }
    public long FramesDropped { get; private set; }
    public SizeInt32 ContentSize => _size;
    public string SourceName => _item?.DisplayName ?? "";

    public WindowCapture()
    {
        ID3D11Device device;
        ID3D11DeviceContext context;
        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            (FeatureLevel[])null!, out device, out context).CheckError();

        _device = device;
        _context = context;

        using var dxgi = _device.QueryInterface<IDXGIDevice>();
        _winrtDevice = Interop.CreateCaptureDevice(dxgi.NativePointer);
    }

    public void Start(GraphicsCaptureItem item)
    {
        _item = item;
        _size = item.Size;

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
        _pool.FrameArrived += OnFrameArrived;

        _item.Closed += (_, _) => { if (!_stopped) Closed?.Invoke(); };

        _session = _pool.CreateCaptureSession(_item);
        TrySuppressBorder(_session);
        _session.StartCapture();
    }

    /// <summary>
    /// Windows draws a yellow border around whatever is being captured. Useful when you
    /// are sharing with other people, noise when you are recording for yourself — and it
    /// would end up in the file. Only newer builds allow turning it off.
    /// </summary>
    private static void TrySuppressBorder(GraphicsCaptureSession session)
    {
        try
        {
            if (Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                    typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.IsBorderRequired)))
                session.IsBorderRequired = false;
        }
        catch { /* older build, or policy says the border stays */ }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame == null) return;

        var arrived = _since.Elapsed;
        if (MinimumInterval > TimeSpan.Zero && arrived - _lastDelivered < MinimumInterval)
        {
            FramesSkipped++;
            return;
        }
        _lastDelivered = arrived;

        try
        {
            if (frame.ContentSize.Width != _size.Width || frame.ContentSize.Height != _size.Height)
            {
                _size = frame.ContentSize;
                ReleaseStaging();
                pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
            }

            int width = Math.Max(1, _size.Width);
            int height = Math.Max(1, _size.Height);
            EnsureStaging(width, height);

            var texturePointer = Interop.GetTexturePointer(frame.Surface);
            using var texture = new ID3D11Texture2D(texturePointer);
            _context.CopyResource(_staging!, texture);

            var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var pixels = new ReadOnlySpan<byte>((void*)mapped.DataPointer, (int)mapped.RowPitch * height);
                FramesReceived++;
                FrameReceived?.Invoke(new CapturedFrame(
                    pixels, width, height, (int)mapped.RowPitch, frame.SystemRelativeTime));
            }
            finally
            {
                _context.Unmap(_staging!, 0);
            }
        }
        catch
        {
            // A frame that cannot be read is not worth tearing the recording down for.
            FramesDropped++;
        }
    }

    private void EnsureStaging(int width, int height)
    {
        if (_staging != null && _staging.Description.Width == width && _staging.Description.Height == height)
            return;

        ReleaseStaging();
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        });
    }

    private void ReleaseStaging()
    {
        _staging?.Dispose();
        _staging = null;
    }

    public void Stop()
    {
        _stopped = true;
        if (_pool != null) _pool.FrameArrived -= OnFrameArrived;
        _session?.Dispose();
        _session = null;
        _pool?.Dispose();
        _pool = null;
    }

    public void Dispose()
    {
        Stop();
        ReleaseStaging();
        _winrtDevice?.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
