#pragma warning disable SYSLIB1054 // Use LibraryImportAttribute — DllImport used intentionally for complex struct marshalling

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace YardStickOne.Api.Internal;

/// <summary>
/// P/Invoke declarations for SetupAPI and WinUSB needed to enumerate and communicate
/// with USB devices via the Windows-native auto-WinUSB driver path.
/// </summary>
internal static class WinUsbNative
{
	// SetupAPI flags
	internal const uint DIGCF_PRESENT = 0x00000002;
	internal const uint DIGCF_DEVICEINTERFACE = 0x00000010;

	// CreateFile constants
	internal const uint GENERIC_READ = 0x80000000;
	internal const uint GENERIC_WRITE = 0x40000000;
	internal const uint FILE_SHARE_READ = 0x00000001;
	internal const uint FILE_SHARE_WRITE = 0x00000002;
	internal const uint OPEN_EXISTING = 3;
	internal const uint FILE_ATTRIBUTE_NORMAL = 0x80;
	internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;

	// WinUSB pipe policy type IDs
	internal const uint SHORT_PACKET_TERMINATE = 0x01;  // OUT: append ZLP if multiple of maxPacketSize
	internal const uint AUTO_CLEAR_STALL      = 0x02;  // IN:  auto-reset stalled pipe
	internal const uint PIPE_TRANSFER_TIMEOUT = 0x03;
	internal const uint ALLOW_PARTIAL_READS   = 0x05;  // IN:  return short reads immediately
	internal const uint AUTO_FLUSH            = 0x06;  // IN:  discard if partial and ALLOW_PARTIAL_READS
	internal const uint RAW_IO                = 0x07;  // bypass WinUSB queuing

	/// <summary>USB device interface class GUID — matches all USB devices regardless of installed driver.</summary>
	internal static readonly Guid GUID_DEVINTERFACE_USB_DEVICE =
		new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

	[StructLayout(LayoutKind.Sequential)]
	internal struct SP_DEVICE_INTERFACE_DATA
	{
		public uint cbSize;
		public Guid InterfaceClassGuid;
		public uint Flags;
		public IntPtr Reserved;
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	internal struct USB_INTERFACE_DESCRIPTOR
	{
		public byte bLength;
		public byte bDescriptorType;
		public byte bInterfaceNumber;
		public byte bAlternateSetting;
		public byte bNumEndpoints;
		public byte bInterfaceClass;
		public byte bInterfaceSubClass;
		public byte bInterfaceProtocol;
		public byte iInterface;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct WINUSB_PIPE_INFORMATION
	{
		public int PipeType;       // USB_PIPE_TYPE enum
		public byte PipeId;
		public ushort MaximumPacketSize;
		public byte Interval;
	}

	// ── SetupAPI ─────────────────────────────────────────────────────────────

	[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	internal static extern IntPtr SetupDiGetClassDevs(
		ref Guid ClassGuid,
		[MarshalAs(UnmanagedType.LPWStr)] string? Enumerator,
		IntPtr hwndParent,
		uint Flags);

	[DllImport("setupapi.dll", SetLastError = true)]
	internal static extern bool SetupDiEnumDeviceInterfaces(
		IntPtr DeviceInfoSet,
		IntPtr DeviceInfoData,
		ref Guid InterfaceClassGuid,
		uint MemberIndex,
		ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

	/// <summary>
	/// Variable-length version: pass <see cref="IntPtr.Zero"/> for <paramref name="DeviceInterfaceDetailData"/>
	/// on first call to obtain <paramref name="RequiredSize"/>, then allocate and call again.
	/// </summary>
	[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	internal static extern bool SetupDiGetDeviceInterfaceDetail(
		IntPtr DeviceInfoSet,
		ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
		IntPtr DeviceInterfaceDetailData,
		uint DeviceInterfaceDetailDataSize,
		out uint RequiredSize,
		IntPtr DeviceInfoData);

	[DllImport("setupapi.dll", SetLastError = true)]
	internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

	// ── Kernel32 ─────────────────────────────────────────────────────────────

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	internal static extern SafeFileHandle CreateFile(
		string lpFileName,
		uint dwDesiredAccess,
		uint dwShareMode,
		IntPtr lpSecurityAttributes,
		uint dwCreationDisposition,
		uint dwFlagsAndAttributes,
		IntPtr hTemplateFile);

	// ── WinUSB ───────────────────────────────────────────────────────────────

	[DllImport("winusb.dll", SetLastError = true)]
	internal static extern bool WinUsb_Initialize(SafeFileHandle DeviceHandle, out IntPtr InterfaceHandle);

	[DllImport("winusb.dll", SetLastError = true)]
	internal static extern bool WinUsb_Free(IntPtr InterfaceHandle);

	[DllImport("winusb.dll", SetLastError = true)]
	internal static extern bool WinUsb_QueryInterfaceSettings(
		IntPtr InterfaceHandle,
		byte AlternateInterfaceNumber,
		out USB_INTERFACE_DESCRIPTOR UsbAltInterfaceDescriptor);

	[DllImport("winusb.dll", SetLastError = true)]
	internal static extern bool WinUsb_QueryPipe(
		IntPtr InterfaceHandle,
		byte AlternateInterfaceNumber,
		byte PipeIndex,
		out WINUSB_PIPE_INFORMATION PipeInformation);

	[DllImport("winusb.dll", SetLastError = true)]
	internal static extern bool WinUsb_SetPipePolicy(
		IntPtr InterfaceHandle,
		byte PipeID,
		uint PolicyType,
		uint ValueLength,
		ref uint Value);

	[DllImport("winusb.dll", SetLastError = true)]
	internal static extern bool WinUsb_WritePipe(
		IntPtr InterfaceHandle,
		byte PipeID,
		byte[] pBuffer,
		uint BufferLength,
		out uint LengthTransferred,
		IntPtr Overlapped);

	[DllImport("winusb.dll", SetLastError = true)]
	internal static extern bool WinUsb_ReadPipe(
		IntPtr InterfaceHandle,
		byte PipeID,
		byte[] pBuffer,
		uint BufferLength,
		out uint LengthTransferred,
		IntPtr Overlapped);

	[DllImport("winusb.dll", SetLastError = true)]
	internal static extern bool WinUsb_ResetPipe(IntPtr InterfaceHandle, byte PipeID);

	[DllImport("winusb.dll", SetLastError = true)]
	internal static extern bool WinUsb_AbortPipe(IntPtr InterfaceHandle, byte PipeID);

	[DllImport("winusb.dll", SetLastError = true)]
	internal static extern bool WinUsb_FlushPipe(IntPtr InterfaceHandle, byte PipeID);
}
