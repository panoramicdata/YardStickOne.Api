namespace YardStickOne.Api.Models;

/// <summary>
/// Configuration options for the <see cref="IYardStickOneClient"/>.
/// </summary>
public sealed class YardStickOneClientOptions
{
	/// <summary>
	/// USB Vendor ID for the YARD Stick One. Do not change unless using a custom firmware.
	/// </summary>
	public int VendorId { get; init; } = 0x1D50;

	/// <summary>
	/// USB Product ID for the YARD Stick One. Do not change unless using a custom firmware.
	/// </summary>
	public int ProductId { get; init; } = 0x605B;

	/// <summary>
	/// Default frequency in Hz used when no explicit frequency is provided.
	/// Defaults to 433.92 MHz.
	/// </summary>
	public double DefaultFrequencyHz { get; init; } = 433_920_000;

	/// <summary>
	/// Default modulation scheme. Defaults to <see cref="Modulation.AskOok"/> which
	/// is required for Somfy RTS and similar OOK-encoded remotes.
	/// </summary>
	public Modulation DefaultModulation { get; init; } = Modulation.AskOok;

	/// <summary>
	/// Default baud rate (symbols per second). Defaults to 4800 bps.
	/// </summary>
	public uint DefaultBaudRate { get; init; } = 4800;

	/// <summary>
	/// Maximum duration to wait for a receive operation before timing out.
	/// </summary>
	public TimeSpan ReceiveTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
