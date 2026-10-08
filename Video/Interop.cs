using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace Jerboa.Video;

/// <summary>
/// The bridge between Direct3D and Windows.Graphics.Capture.
///
/// Both sides speak COM, but not the same dialect: Direct3D objects are classic runtime
/// callable wrappers, while the capture API arrives through the WinRT projection, and the
/// runtime will not cast one into the other. So everything crosses as a raw pointer and is
/// rewrapped on the far side.
/// </summary>
internal static class Interop
{
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow([In] IntPtr window, [In] ref Guid iid, out IntPtr result);
        [PreserveSig] int CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid, out IntPtr result);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig] int GetInterface([In] ref Guid iid, out IntPtr result);
    }

    [ComImport]
    [Guid("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithWindow
    {
        void Initialize(IntPtr hwnd);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    // The IIDs of the WinRT interfaces, which are not the GUIDs the .NET projection
    // carries on the corresponding classes.
    private static Guid _captureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static Guid _texture2dIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    /// <summary>Wraps a Direct3D device so the capture API will accept it.</summary>
    public static IDirect3DDevice CreateCaptureDevice(IntPtr dxgiDevice)
    {
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var pointer));
        try { return WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    /// <summary>A capture item for one top-level window.</summary>
    public static GraphicsCaptureItem CreateItemForWindow(IntPtr window)
    {
        var factory = GetActivationFactory<IGraphicsCaptureItemInterop>();
        Marshal.ThrowExceptionForHR(factory.CreateForWindow(window, ref _captureItemIid, out var pointer));
        return Adopt(pointer);
    }

    /// <summary>A capture item for one monitor.</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
    {
        var factory = GetActivationFactory<IGraphicsCaptureItemInterop>();
        Marshal.ThrowExceptionForHR(factory.CreateForMonitor(monitor, ref _captureItemIid, out var pointer));
        return Adopt(pointer);
    }

    /// <summary>The Direct3D texture behind a captured frame's surface.</summary>
    public static IntPtr GetTexturePointer(IDirect3DSurface surface)
    {
        var abi = WinRT.MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
        try
        {
            // GetObjectForIUnknown is the one place the two dialects meet: it produces a
            // classic wrapper that can be cast to the hand-declared interface above.
            var wrapper = Marshal.GetObjectForIUnknown(abi);
            try
            {
                var access = (IDirect3DDxgiInterfaceAccess)wrapper;
                Marshal.ThrowExceptionForHR(access.GetInterface(ref _texture2dIid, out var texture));
                return texture;
            }
            finally { Marshal.ReleaseComObject(wrapper); }
        }
        finally { Marshal.Release(abi); }
    }

    /// <summary>The system picker is modal and needs to know whose child it is.</summary>
    public static void SetOwner(GraphicsCapturePicker picker, IntPtr owner)
    {
        var abi = WinRT.MarshalInspectable<GraphicsCapturePicker>.FromManaged(picker);
        try
        {
            var wrapper = Marshal.GetObjectForIUnknown(abi);
            try { ((IInitializeWithWindow)wrapper).Initialize(owner); }
            finally { Marshal.ReleaseComObject(wrapper); }
        }
        finally { Marshal.Release(abi); }
    }

    private static GraphicsCaptureItem Adopt(IntPtr pointer)
    {
        try { return GraphicsCaptureItem.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    private static T GetActivationFactory<T>() =>
        WinRT.ActivationFactory.Get(typeof(GraphicsCaptureItem).FullName!).AsInterface<T>();
}
