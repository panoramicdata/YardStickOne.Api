using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace YardStickOne.Api.Internal;

/// <summary>
/// <see cref="IWinUsbPipe"/> backed by the Windows WinUSB API.
/// </summary>
internal sealed class NativeWinUsbPipe(SafeFileHandle deviceHandle, IntPtr winUsbHandle) : IWinUsbPipe
{
	/// <inheritdoc/>
	public bool Write(byte endpoint, byte[] buffer, out uint transferred, out int error)
	{
		var ok = WinUsbNative.WinUsb_WritePipe(winUsbHandle, endpoint, buffer, (uint)buffer.Length, out transferred, IntPtr.Zero);
		error = ok ? 0 : Marshal.GetLastWin32Error();
		return ok;
	}

	/// <inheritdoc/>
	public bool Read(byte endpoint, byte[] buffer, out uint transferred, out int error)
	{
		var ok = WinUsbNative.WinUsb_ReadPipe(winUsbHandle, endpoint, buffer, (uint)buffer.Length, out transferred, IntPtr.Zero);
		error = ok ? 0 : Marshal.GetLastWin32Error();
		return ok;
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		WinUsbNative.WinUsb_Free(winUsbHandle);
		deviceHandle.Dispose();
	}
}
