using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace AirGlass.Capture;

/// <summary>
/// The Direct3D device shared by capture and encoding, and the interop that
/// connects it to Windows.Graphics.Capture (a WinRT API that hands out
/// Direct3D surfaces).
/// </summary>
internal static unsafe class Direct3D
{
    private static readonly Guid GraphicsCaptureItemInteropId = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid GraphicsCaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiInterfaceAccessId = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid Texture2DId = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    /// <summary>A hardware device with video processing; the capture frame
    /// pool and the encoder use it from different threads.</summary>
    public static ID3D11Device CreateDevice()
    {
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        var result = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, flags, levels, out ID3D11Device? device);
        if (result.Failure || device == null)
        {
            result = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, flags, levels, out device);
            result.CheckError();
        }
        using (var multithread = device!.QueryInterface<ID3D11Multithread>())
        {
            multithread.SetMultithreadProtected(true);
        }
        return device;
    }

    /// <summary>The WinRT wrapper of a Direct3D device, for the capture frame pool.</summary>
    public static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>The texture behind a captured frame's surface.</summary>
    public static ID3D11Texture2D TextureFrom(IDirect3DSurface surface)
    {
        var unknown = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var accessId = DxgiInterfaceAccessId;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref accessId, out var access));
            try
            {
                // IDirect3DDxgiInterfaceAccess::GetInterface
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)access)[3];
                var textureId = Texture2DId;
                IntPtr texture;
                Marshal.ThrowExceptionForHR(getInterface(access, &textureId, &texture));
                return new ID3D11Texture2D(texture);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>A capture item for a whole monitor, without the system picker.</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
    {
        var factory = GetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", GraphicsCaptureItemInteropId);
        try
        {
            // IGraphicsCaptureItemInterop::CreateForMonitor
            var createForMonitor = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)factory)[4];
            var itemId = GraphicsCaptureItemId;
            IntPtr item;
            Marshal.ThrowExceptionForHR(createForMonitor(factory, monitor, &itemId, &item));
            try
            {
                return GraphicsCaptureItem.FromAbi(item);
            }
            finally
            {
                Marshal.Release(item);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    private static IntPtr GetActivationFactory(string className, Guid interfaceId)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var classId));
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(classId, ref interfaceId, out var factory));
            return factory;
        }
        finally
        {
            WindowsDeleteString(classId);
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);
}
