using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace SteamCN.Windows;

public class WgcCapturer : IDisposable
{
	[ComImport]
	[Guid("3628DB81-0736-4A94-A22E-6BF1CF0A2B3D")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IGraphicsCaptureItemInterop
	{
		nint CreateForWindow(nint window, ref Guid iid);

		nint CreateForMonitor(nint monitor, ref Guid iid);
	}

	[ComImport]
	[Guid("96E6D076-DE97-425C-9D8A-B8B1D8E8B4B8")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IGraphicsCaptureItemStatic
	{
	}

	private IDirect3DDevice? _device;

	private GraphicsCaptureItem? _item;

	private Direct3D11CaptureFramePool? _framePool;

	private GraphicsCaptureSession? _session;

	private int _frameCount;

	private bool _disposed;

	public static bool IsSupported()
	{
		try
		{
			return GraphicsCaptureSession.IsSupported();
		}
		catch
		{
			return false;
		}
	}

	public async Task<SoftwareBitmap?> CaptureAsync(nint windowHandle)
	{
		if (!IsSupported())
		{
			return null;
		}
		try
		{
			if (_device == null)
			{
				_device = D3D11Interop.CreateDevice();
			}
			GraphicsCaptureItem graphicsCaptureItem = CreateItemForWindow(windowHandle);
			if (graphicsCaptureItem == null)
			{
				return null;
			}
			_item = graphicsCaptureItem;
			SizeInt32 size = graphicsCaptureItem.Size;
			_framePool = Direct3D11CaptureFramePool.Create(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
			_session = _framePool.CreateCaptureSession(graphicsCaptureItem);
			TaskCompletionSource<SoftwareBitmap?> tcs = new TaskCompletionSource<SoftwareBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
			_framePool.FrameArrived += (Direct3D11CaptureFramePool sender, object _) =>
			{
				try
				{
					using Direct3D11CaptureFrame direct3D11CaptureFrame = sender.TryGetNextFrame();
					if (!(direct3D11CaptureFrame == null) && Interlocked.Increment(ref _frameCount) > 5)
					{
						SoftwareBitmap result2 = SoftwareBitmap.CreateCopyFromSurfaceAsync(direct3D11CaptureFrame.Surface).AsTask().GetAwaiter()
							.GetResult();
						tcs.TrySetResult(result2);
					}
				}
				catch (Exception exception)
				{
					tcs.TrySetException(exception);
				}
			};
			_frameCount = 0;
			_session.StartCapture();
			if (await Task.WhenAny(tcs.Task, Task.Delay(3000)) != tcs.Task)
			{
				tcs.TrySetResult(null);
			}
			SoftwareBitmap result = await tcs.Task;
			_session?.Dispose();
			_framePool?.Dispose();
			_session = null;
			_framePool = null;
			return result;
		}
		catch
		{
			return null;
		}
	}

	private static GraphicsCaptureItem? CreateItemForWindow(nint windowHandle)
	{
		try
		{
			Guid iid = new Guid("3628DB81-0736-4A94-A22E-6BF1CF0A2B3D");
			nint num = RoGetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", ref iid);
			if (num == IntPtr.Zero)
			{
				return null;
			}
			try
			{
				IGraphicsCaptureItemInterop graphicsCaptureItemInterop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(num);
				Guid iid2 = new Guid("79C3F95B-31F7-4EC2-A464-632EF5FD30C6");
				nint num2 = graphicsCaptureItemInterop.CreateForWindow(windowHandle, ref iid2);
				if (num2 == IntPtr.Zero)
				{
					return null;
				}
				try
				{
					return Marshal.GetObjectForIUnknown(num2) as GraphicsCaptureItem;
				}
				finally
				{
					Marshal.Release(num2);
				}
			}
			finally
			{
				Marshal.Release(num);
			}
		}
		catch
		{
			return null;
		}
	}

	[DllImport("combase.dll", PreserveSig = false)]
	private static extern nint RoGetActivationFactory([MarshalAs(UnmanagedType.HString)] string activatableClassId, [In] ref Guid iid);

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			_session?.Dispose();
			_framePool?.Dispose();
			GC.SuppressFinalize(this);
		}
	}
}
