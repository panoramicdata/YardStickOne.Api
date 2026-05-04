namespace YardStickOne.Api.Internal;

/// <summary>
/// Abstraction over the raw USB bulk-transfer endpoints of the YARD Stick One.
/// Enables unit testing without a physical device.
/// </summary>
internal interface IUsbTransport : IDisposable
{
	/// <summary>Sends a command byte followed by optional payload.</summary>
	Task SendCommandAsync(byte command, byte[]? payload = null, CancellationToken cancellationToken = default);

	/// <summary>Reads a response from the device bulk-in endpoint.</summary>
	Task<byte[]> ReadResponseAsync(int maxLength = 64, CancellationToken cancellationToken = default);
}
