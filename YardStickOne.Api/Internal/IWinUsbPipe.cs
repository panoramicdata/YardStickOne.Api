namespace YardStickOne.Api.Internal;

/// <summary>
/// Raw bulk read/write access to an opened WinUSB interface; lets the framing logic run without hardware.
/// </summary>
internal interface IWinUsbPipe : IDisposable
{
	/// <summary>Writes a buffer to an OUT endpoint. On failure <paramref name="error"/> holds the Win32 error code.</summary>
	bool Write(byte endpoint, byte[] buffer, out uint transferred, out int error);

	/// <summary>Reads from an IN endpoint into a buffer. On failure <paramref name="error"/> holds the Win32 error code.</summary>
	bool Read(byte endpoint, byte[] buffer, out uint transferred, out int error);
}
