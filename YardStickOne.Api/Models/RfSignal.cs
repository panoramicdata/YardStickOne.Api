namespace YardStickOne.Api.Models;

/// <summary>
/// Represents a recorded RF signal captured by the YARD Stick One.
/// </summary>
public sealed class RfSignal
{
	/// <summary>
	/// The frequency (in Hz) at which the signal was captured.
	/// </summary>
	public double FrequencyHz { get; init; }

	/// <summary>
	/// The raw IQ or OOK sample bytes as received from the device.
	/// </summary>
	public required byte[] Data { get; init; }

	/// <summary>
	/// The UTC timestamp when the capture started.
	/// </summary>
	public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

	/// <summary>
	/// The modulation scheme used during capture.
	/// </summary>
	public Modulation Modulation { get; init; }

	/// <summary>
	/// The baud rate (symbols per second) used during capture.
	/// </summary>
	public uint BaudRate { get; init; }
}
