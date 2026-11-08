using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using YardStickOne.Api.Models;

namespace YardStickOne.Api;

/// <summary>
/// Main client interface for interacting with a YARD Stick One USB radio device.
/// </summary>
public interface IYardStickOneClient : IDisposable
{
	/// <summary>
	/// Gets the current operating frequency in Hz.
	/// </summary>
	double FrequencyHz { get; }

	/// <summary>
	/// Gets the current modulation scheme.
	/// </summary>
	Modulation Modulation { get; }

	/// <summary>
	/// Gets the current baud rate (symbols per second).
	/// </summary>
	uint BaudRate { get; }

	/// <summary>
	/// Configures the radio for the given frequency, modulation, and baud rate.
	/// </summary>
	/// <param name="frequencyHz">Frequency in Hz.</param>
	/// <param name="modulation">Modulation scheme.</param>
	/// <param name="baudRate">Baud rate in symbols per second.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	[SuppressMessage("Major Code Smell", "S2360", Justification = "Published public interface; an optional CancellationToken is the .NET convention and removing it would break existing callers and implementers.")]
	Task ConfigureAsync(
		double frequencyHz,
		Modulation modulation,
		uint baudRate,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Receives a single RF signal burst, waiting up to the configured receive timeout.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The captured <see cref="RfSignal"/>.</returns>
	[SuppressMessage("Major Code Smell", "S2360", Justification = "Published public interface; an optional CancellationToken is the .NET convention and removing it would break existing callers and implementers.")]
	Task<RfSignal> ReceiveAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Transmits the given RF signal.
	/// </summary>
	/// <param name="signal">The signal to transmit.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	[SuppressMessage("Major Code Smell", "S2360", Justification = "Published public interface; an optional CancellationToken is the .NET convention and removing it would break existing callers and implementers.")]
	Task TransmitAsync(RfSignal signal, CancellationToken cancellationToken = default);

	/// <summary>
	/// Transmits the given raw bytes at the currently configured frequency, modulation, and baud rate.
	/// </summary>
	/// <param name="data">Raw bytes to transmit.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	[SuppressMessage("Major Code Smell", "S2360", Justification = "Published public interface; an optional CancellationToken is the .NET convention and removing it would break existing callers and implementers.")]
	Task TransmitAsync(byte[] data, CancellationToken cancellationToken = default);

	/// <summary>
	/// Resets the device to its default state.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token.</param>
	[SuppressMessage("Major Code Smell", "S2360", Justification = "Published public interface; an optional CancellationToken is the .NET convention and removing it would break existing callers and implementers.")]
	Task ResetAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Continuously streams raw RF samples, keeping the radio in RX mode until the token is cancelled.
	/// Each yielded <see cref="RfSignal"/> contains one FIFO burst from the CC1111.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token; cancel to stop the stream and return to idle.</param>
	[SuppressMessage("Major Code Smell", "S2360", Justification = "Published public interface; an optional CancellationToken is the .NET convention and removing it would break existing callers and implementers.")]
	IAsyncEnumerable<RfSignal> StreamAsync(CancellationToken cancellationToken = default);
}
