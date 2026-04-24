using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace zRover.WinUI.Capabilities
{
    /// <summary>
    /// Minimal D3D11 + Windows.Graphics.Capture interop glue used by
    /// <see cref="FrameCaptureCapability"/>. Creates a hardware
    /// <see cref="IDirect3DDevice"/> for the capture frame pool and a
    /// <see cref="GraphicsCaptureItem"/> targeting a specific HWND, both of
    /// which require COM interop that is not exposed by the WinRT projection.
    /// </summary>
    internal static class FrameCaptureHelpers
    {
        [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int D3D11CreateDevice(
            IntPtr pAdapter,
            int driverType,
            IntPtr software,
            uint flags,
            IntPtr pFeatureLevels,
            uint featureLevels,
            uint sdkVersion,
            out IntPtr ppDevice,
            out int featureLevel,
            out IntPtr ppImmediateContext);

        [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(
            IntPtr dxgiDevice,
            out IntPtr graphicsDevice);

        private const int D3D_DRIVER_TYPE_HARDWARE = 1;
        private const int D3D_DRIVER_TYPE_WARP = 5;
        private const uint D3D11_SDK_VERSION = 7;
        private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;

        // IDXGIDevice
        private static readonly Guid IID_IDXGIDevice =
            new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

        // IGraphicsCaptureItemInterop
        [ComImport]
        [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            IntPtr CreateForWindow(
                [In] IntPtr window,
                [In] ref Guid iid);

            IntPtr CreateForMonitor(
                [In] IntPtr monitor,
                [In] ref Guid iid);
        }

        // Windows.Graphics.Capture.IGraphicsCaptureItem
        private static readonly Guid IID_IGraphicsCaptureItem =
            new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");

        /// <summary>
        /// Creates a hardware-backed <see cref="IDirect3DDevice"/> suitable for
        /// passing to <c>Direct3D11CaptureFramePool.CreateFreeThreaded</c>.
        /// Falls back to WARP if hardware creation fails.
        /// </summary>
        public static IDirect3DDevice CreateDirect3DDevice()
        {
            int hr = D3D11CreateDevice(
                IntPtr.Zero,
                D3D_DRIVER_TYPE_HARDWARE,
                IntPtr.Zero,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero,
                0,
                D3D11_SDK_VERSION,
                out IntPtr d3dDevice,
                out _,
                out IntPtr d3dContext);

            if (hr < 0)
            {
                hr = D3D11CreateDevice(
                    IntPtr.Zero,
                    D3D_DRIVER_TYPE_WARP,
                    IntPtr.Zero,
                    D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    IntPtr.Zero,
                    0,
                    D3D11_SDK_VERSION,
                    out d3dDevice,
                    out _,
                    out d3dContext);

                if (hr < 0)
                    throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException("D3D11CreateDevice failed.");
            }

            // The immediate context is unused by this code path; release it.
            if (d3dContext != IntPtr.Zero)
                Marshal.Release(d3dContext);

            try
            {
                Guid dxgiIid = IID_IDXGIDevice;
                int qiHr = Marshal.QueryInterface(d3dDevice, ref dxgiIid, out IntPtr dxgiDevice);
                if (qiHr < 0)
                    throw Marshal.GetExceptionForHR(qiHr) ?? new InvalidOperationException("QI for IDXGIDevice failed.");

                try
                {
                    int wrapHr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr graphicsDevice);
                    if (wrapHr < 0)
                        throw Marshal.GetExceptionForHR(wrapHr) ?? new InvalidOperationException("CreateDirect3D11DeviceFromDXGIDevice failed.");

                    try
                    {
                        return MarshalInspectable<IDirect3DDevice>.FromAbi(graphicsDevice);
                    }
                    finally
                    {
                        Marshal.Release(graphicsDevice);
                    }
                }
                finally
                {
                    Marshal.Release(dxgiDevice);
                }
            }
            finally
            {
                Marshal.Release(d3dDevice);
            }
        }

        [DllImport("combase.dll", PreserveSig = false)]
        private static extern void RoGetActivationFactory(
            IntPtr activatableClassId,
            [In] ref Guid iid,
            out IntPtr factory);

        [DllImport("combase.dll", PreserveSig = false)]
        private static extern void WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
            int length,
            out IntPtr hstring);

        [DllImport("combase.dll", PreserveSig = false)]
        private static extern void WindowsDeleteString(IntPtr hstring);

        /// <summary>
        /// Creates a <see cref="GraphicsCaptureItem"/> targeting the specified
        /// top-level window, without showing a picker UI. Permitted for the
        /// caller's own HWND in WinUI 3 / desktop apps.
        /// </summary>
        public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
        {
            const string ClassId = "Windows.Graphics.Capture.GraphicsCaptureItem";
            WindowsCreateString(ClassId, ClassId.Length, out IntPtr hstring);
            IntPtr factoryPtr;
            try
            {
                Guid interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
                RoGetActivationFactory(hstring, ref interopIid, out factoryPtr);
            }
            finally
            {
                WindowsDeleteString(hstring);
            }

            try
            {
                var factory = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
                Guid iid = IID_IGraphicsCaptureItem;
                IntPtr abi = factory.CreateForWindow(hwnd, ref iid);
                try
                {
                    return MarshalInspectable<GraphicsCaptureItem>.FromAbi(abi);
                }
                finally
                {
                    Marshal.Release(abi);
                }
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
    }
}
