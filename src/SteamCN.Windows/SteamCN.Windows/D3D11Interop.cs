using System;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;

namespace SteamCN.Windows;

internal static class D3D11Interop
{
	[ComImport]
	[Guid("aec22fb8-76f3-4639-9be0-56922b3b3c0e")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IDXGIDevice
	{
		[PreserveSig]
		int SetPrivateData(nint name, uint dataSize, nint pData);

		[PreserveSig]
		int SetPrivateDataInterface(nint name, nint pUnk);

		[PreserveSig]
		int GetPrivateData(nint name, ref uint pDataSize, nint pData);

		[PreserveSig]
		int GetParent(ref Guid riid, out nint ppvParent);

		[PreserveSig]
		int GetAdapter(out nint pAdapter);

		[PreserveSig]
		int CreateSurface(nint pDesc, uint numSurfaces, uint usage, nint pSharedResource, out nint ppSurface);

		[PreserveSig]
		int QueryResourceResidency(nint[] ppResources, nint[] pResidencyStatus, uint numResources);
	}

	[ComImport]
	[Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IInspectable
	{
		[PreserveSig]
		int GetIids(out uint iidCount, out nint iids);

		[PreserveSig]
		int GetRuntimeClassName(out nint className);

		[PreserveSig]
		int GetTrustLevel(out int trustLevel);
	}

	private const int D3D_DRIVER_TYPE_HARDWARE = 0;

	private const int D3D_DRIVER_TYPE_WARP = 5;

	private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 32u;

	private const uint D3D11_SDK_VERSION = 7u;

	[DllImport("d3d11.dll", SetLastError = true)]
	private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

	[DllImport("d3d11.dll")]
	private static extern int D3D11CreateDevice(nint pAdapter, int driverType, nint software, uint flags, int[]? pFeatureLevels, uint featureLevels, uint sdkVersion, out nint ppDevice, out int pFeatureLevel, out nint ppImmediateContext);

	public static IDirect3DDevice CreateDevice()
	{
		nint ppDevice = IntPtr.Zero;
		nint ppImmediateContext = IntPtr.Zero;
		int num = D3D11CreateDevice(IntPtr.Zero, 0, IntPtr.Zero, 32u, null, 0u, 7u, out ppDevice, out var pFeatureLevel, out ppImmediateContext);
		if (num < 0)
		{
			num = D3D11CreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 32u, null, 0u, 7u, out ppDevice, out pFeatureLevel, out ppImmediateContext);
		}
		if (num < 0 || ppDevice == IntPtr.Zero)
		{
			throw new InvalidOperationException($"D3D11CreateDevice failed: 0x{num:X8}");
		}
		try
		{
			Guid iid = new Guid("54ec77fa-1377-44e6-8c32-88fd52f4b958");
			int num2 = Marshal.QueryInterface(ppDevice, in iid, out var ppv);
			if (num2 < 0 || ppv == IntPtr.Zero)
			{
				throw new InvalidOperationException($"QueryInterface for IDXGIDevice failed: 0x{num2:X8}");
			}
			try
			{
				num = CreateDirect3D11DeviceFromDXGIDevice(ppv, out var graphicsDevice);
				if (num < 0 || graphicsDevice == IntPtr.Zero)
				{
					throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice failed: 0x{num:X8}");
				}
				try
				{
					return (IDirect3DDevice)(IInspectable)Marshal.GetObjectForIUnknown(graphicsDevice);
				}
				finally
				{
					Marshal.Release(graphicsDevice);
				}
			}
			finally
			{
				Marshal.Release(ppv);
			}
		}
		finally
		{
			if (ppImmediateContext != IntPtr.Zero)
			{
				Marshal.Release(ppImmediateContext);
			}
			if (ppDevice != IntPtr.Zero)
			{
				Marshal.Release(ppDevice);
			}
		}
	}
}
