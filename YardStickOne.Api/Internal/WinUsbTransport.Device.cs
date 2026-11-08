using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using YardStickOne.Api.Exceptions;
using YardStickOne.Api.Models;

namespace YardStickOne.Api.Internal;

/// <summary>
/// Device discovery, opening and startup recovery for the WinUSB transport.
/// </summary>
internal sealed partial class WinUsbTransport
{
	/// <summary>Locates and opens the YARD Stick One using SetupAPI + WinUSB.</summary>
	/// <exception cref="YardStickOneNotFoundException">Device not found or could not be opened.</exception>
	internal static WinUsbTransport Open(YardStickOneClientOptions options, ILogger logger)
	{
		var path = FindDevicePath(options.VendorId, options.ProductId)
		?? throw new YardStickOneNotFoundException();

		var deviceHandle = WinUsbNative.CreateFile(
		path,
		WinUsbNative.GENERIC_READ | WinUsbNative.GENERIC_WRITE,
		WinUsbNative.FILE_SHARE_READ | WinUsbNative.FILE_SHARE_WRITE,
		IntPtr.Zero,
		WinUsbNative.OPEN_EXISTING,
		WinUsbNative.FILE_FLAG_OVERLAPPED,
		IntPtr.Zero);

		if (deviceHandle.IsInvalid)
		{
			throw new YardStickOneNotFoundException();
		}

		if (!WinUsbNative.WinUsb_Initialize(deviceHandle, out var winUsbHandle))
		{
			deviceHandle.Dispose();
			throw new YardStickOneCommandException(
			$"WinUsb_Initialize failed: 0x{Marshal.GetLastWin32Error():X8}");
		}

		var (epOut, epIn) = DiscoverEndpoints(winUsbHandle, logger);

		// Set pipe policies.
		uint timeout1000 = 1000u;
		uint one = 1u;
		uint zero = 0u;
		// OUT: 1-second transfer timeout; append ZLP if payload is multiple of maxPacket.
		WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epOut, WinUsbNative.PIPE_TRANSFER_TIMEOUT, 4, ref timeout1000);
		WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epOut, WinUsbNative.SHORT_PACKET_TERMINATE, 4, ref one);
		WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epOut, WinUsbNative.RAW_IO, 4, ref one);
		// IN: 1-second timeout; auto-clear stall; allow partial reads; raw IO.
		WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epIn, WinUsbNative.PIPE_TRANSFER_TIMEOUT, 4, ref timeout1000);
		WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epIn, WinUsbNative.AUTO_CLEAR_STALL, 4, ref one);
		WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epIn, WinUsbNative.ALLOW_PARTIAL_READS, 4, ref one);
		WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epIn, WinUsbNative.AUTO_FLUSH, 4, ref zero);
		WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epIn, WinUsbNative.RAW_IO, 4, ref one);

		// Flush and reset both pipes to clear stale state from previous runs.
		WinUsbNative.WinUsb_AbortPipe(winUsbHandle, epIn);
		WinUsbNative.WinUsb_FlushPipe(winUsbHandle, epIn);
		WinUsbNative.WinUsb_ResetPipe(winUsbHandle, epOut);
		WinUsbNative.WinUsb_ResetPipe(winUsbHandle, epIn);

		// Best-effort startup recovery: force SIDLE then drain stale IN frames.
		uint shortTimeout = 200u;
		WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epIn, WinUsbNative.PIPE_TRANSFER_TIMEOUT, 4, ref shortTimeout);
		TrySendIdleAndDrain(winUsbHandle, epOut, epIn);
		WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epIn, WinUsbNative.PIPE_TRANSFER_TIMEOUT, 4, ref timeout1000);

		LogOpened(logger, options.VendorId, options.ProductId, path);
		return new WinUsbTransport(deviceHandle, winUsbHandle, epOut, epIn, logger);
	}

	/// <summary>Returns the device path for the first matching VID/PID, or null if not found.</summary>
	internal static string? FindDevicePath(int vid, int pid)
	{
		var needle = $"vid_{vid:x4}&pid_{pid:x4}";
		foreach (var path in EnumerateAllDevicePaths())
		{
			if (path.Contains(needle, StringComparison.OrdinalIgnoreCase))
			{
				return path;
			}
		}

		return null;
	}

	/// <summary>Returns paths for every USB device currently visible to SetupAPI.</summary>
	internal static IReadOnlyList<string> EnumerateAllDevicePaths()
	{
		var results = new List<string>();
		var guid = WinUsbNative.GUID_DEVINTERFACE_USB_DEVICE;

		var devInfo = WinUsbNative.SetupDiGetClassDevs(
		ref guid, null, IntPtr.Zero,
		WinUsbNative.DIGCF_PRESENT | WinUsbNative.DIGCF_DEVICEINTERFACE);

		if (devInfo == new IntPtr(-1))
		{
			return results;
		}

		try
		{
			var ifaceData = new WinUsbNative.SP_DEVICE_INTERFACE_DATA
			{
				cbSize = (uint)Marshal.SizeOf<WinUsbNative.SP_DEVICE_INTERFACE_DATA>(),
			};

			for (uint index = 0;
			WinUsbNative.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref guid, index, ref ifaceData);
			index++)
			{
				var path = ReadDeviceInterfacePath(devInfo, ref ifaceData);
				if (!string.IsNullOrEmpty(path))
				{
					results.Add(path);
				}
			}
		}
		finally
		{
			WinUsbNative.SetupDiDestroyDeviceInfoList(devInfo);
		}

		return results;
	}

	private static string? ReadDeviceInterfacePath(IntPtr devInfo, ref WinUsbNative.SP_DEVICE_INTERFACE_DATA ifaceData)
	{
		WinUsbNative.SetupDiGetDeviceInterfaceDetail(
		devInfo, ref ifaceData, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);

		if (requiredSize == 0)
		{
			return null;
		}

		var buffer = Marshal.AllocHGlobal((int)requiredSize);
		try
		{
			Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);

			if (!WinUsbNative.SetupDiGetDeviceInterfaceDetail(
			devInfo, ref ifaceData, buffer, requiredSize, out _, IntPtr.Zero))
			{
				return null;
			}

			return Marshal.PtrToStringUni(buffer + 4);
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}

	private static void TrySendIdleAndDrain(IntPtr winUsbHandle, byte epOut, byte epIn)
	{
		try
		{
			// Retry SIDLE because the OUT pipe may NAK while the device is mid-RX interrupt.
			byte[] frame = [AppSystem, SysCmdRfMode, 1, 0, RfstSidle];
			for (var attempt = 0; attempt < 5; attempt++)
			{
				if (WinUsbNative.WinUsb_WritePipe(winUsbHandle, epOut, frame, (uint)frame.Length, out _, IntPtr.Zero))
				{
					break;
				}

				Thread.Sleep(100);
			}

			// Drain the IN pipe until it is quiet: keep reading until we get two
			// consecutive empty reads (each times out after 200 ms), which means
			// the device has stopped streaming stale RF frames.
			var buf = new byte[512];
			int emptyStreak = 0;
			while (emptyStreak < 2)
			{
				if (!WinUsbNative.WinUsb_ReadPipe(winUsbHandle, epIn, buf, (uint)buf.Length, out var n, IntPtr.Zero) || n == 0)
				{
					emptyStreak++;
				}
				else
				{
					emptyStreak = 0;
				}
			}
		}
		catch
		{
			// Best effort only.
		}
	}

	private static (byte epOut, byte epIn) DiscoverEndpoints(IntPtr winUsbHandle, ILogger logger)
	{
		if (!WinUsbNative.WinUsb_QueryInterfaceSettings(winUsbHandle, 0, out var ifaceDesc))
		{
			LogEndpoints(logger, DefaultEndpointOut, DefaultEndpointIn);
			return (DefaultEndpointOut, DefaultEndpointIn);
		}

		byte epOut = DefaultEndpointOut, epIn = DefaultEndpointIn;
		for (byte i = 0; i < ifaceDesc.bNumEndpoints; i++)
		{
			if (!WinUsbNative.WinUsb_QueryPipe(winUsbHandle, 0, i, out var pipe))
			{
				continue;
			}

			if (logger.IsEnabled(LogLevel.Debug))
			{
				var pipeType = PipeTypeName(pipe.PipeType);
				LogPipe(logger, i, pipe.PipeId, pipeType, pipe.MaximumPacketSize);
			}

			if (pipe.PipeType != UsbdPipeTypeBulk)
			{
				continue;
			}

			if ((pipe.PipeId & 0x80) != 0)
			{
				epIn = pipe.PipeId;
			}
			else
			{
				epOut = pipe.PipeId;
			}
		}

		LogEndpoints(logger, epOut, epIn);
		return (epOut, epIn);
	}

	private static string PipeTypeName(int pipeType)
	=> pipeType switch
	{
		0 => "Control",
		1 => "Isochronous",
		2 => "Bulk",
		3 => "Interrupt",
		_ => $"Unknown({pipeType})",
	};
}
