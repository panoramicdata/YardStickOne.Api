using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YardStickOne.Api.Exceptions;
using YardStickOne.Api.Internal;
using YardStickOne.Api.Models;

namespace YardStickOne.Api;

/// <summary>
/// Concrete implementation of <see cref="IYardStickOneClient"/> that communicates
/// with the physical YARD Stick One over USB bulk transfer using the rfcat protocol.
/// </summary>
public sealed partial class YardStickOneClient : IYardStickOneClient
{
private readonly IUsbTransport _transport;
private readonly YardStickOneClientOptions _options;
private readonly ILogger<YardStickOneClient> _logger;
private bool _disposed;

/// <inheritdoc/>
public double FrequencyHz { get; private set; }

/// <inheritdoc/>
public Modulation Modulation { get; private set; }

/// <inheritdoc/>
public uint BaudRate { get; private set; }

/// <summary>
/// Opens a connection to the first YARD Stick One found on the USB bus,
/// using the supplied options and logger.
/// </summary>
/// <exception cref="YardStickOneNotFoundException">No device is attached.</exception>
public YardStickOneClient(
YardStickOneClientOptions? options = null,
ILogger<YardStickOneClient>? logger = null)
: this(
options ?? new YardStickOneClientOptions(),
logger ?? NullLogger<YardStickOneClient>.Instance,
transport: null)
{
}

/// <summary>
/// Constructor used by tests; accepts an injected transport.
/// </summary>
internal YardStickOneClient(
YardStickOneClientOptions options,
ILogger<YardStickOneClient> logger,
IUsbTransport? transport)
{
_options = options;
_logger = logger;
_transport = transport ?? LibUsbTransport.Open(options, logger);

FrequencyHz = options.DefaultFrequencyHz;
Modulation = options.DefaultModulation;
BaudRate = options.DefaultBaudRate;
}

/// <inheritdoc/>
public async Task ConfigureAsync(
double frequencyHz,
Modulation modulation,
uint baudRate,
CancellationToken cancellationToken = default)
{
ObjectDisposedException.ThrowIf(_disposed, this);

LogConfiguring(_logger, frequencyHz / 1_000_000, modulation, baudRate);

await SetFrequencyAsync(frequencyHz, cancellationToken).ConfigureAwait(false);
await SetModulationAsync(modulation, cancellationToken).ConfigureAwait(false);
await SetBaudRateAsync(baudRate, cancellationToken).ConfigureAwait(false);

FrequencyHz = frequencyHz;
Modulation = modulation;
BaudRate = baudRate;
}

/// <inheritdoc/>
public async Task<RfSignal> ReceiveAsync(CancellationToken cancellationToken = default)
{
ObjectDisposedException.ThrowIf(_disposed, this);

LogStartingReceive(_logger, FrequencyHz / 1_000_000, Modulation, BaudRate);

// Enter RX mode
await _transport.SendCommandAsync(RfCatCommands.RxMode, cancellationToken: cancellationToken)
.ConfigureAwait(false);

using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
timeoutCts.CancelAfter(_options.ReceiveTimeout);

// Poll for data
var data = await _transport.ReadResponseAsync(512, timeoutCts.Token).ConfigureAwait(false);

// Return to idle
await _transport.SendCommandAsync(RfCatCommands.IdleMode, cancellationToken: cancellationToken)
.ConfigureAwait(false);

LogReceived(_logger, data.Length);

return new RfSignal
{
FrequencyHz = FrequencyHz,
Modulation = Modulation,
BaudRate = BaudRate,
Data = data,
CapturedAt = DateTimeOffset.UtcNow,
};
}

/// <inheritdoc/>
public async Task TransmitAsync(RfSignal signal, CancellationToken cancellationToken = default)
{
ArgumentNullException.ThrowIfNull(signal);

// Re-configure if the signal was recorded at different settings
if (signal.FrequencyHz != FrequencyHz
|| signal.Modulation != Modulation
|| signal.BaudRate != BaudRate)
{
await ConfigureAsync(signal.FrequencyHz, signal.Modulation, signal.BaudRate, cancellationToken)
.ConfigureAwait(false);
}

await TransmitAsync(signal.Data, cancellationToken).ConfigureAwait(false);
}

/// <inheritdoc/>
public async Task TransmitAsync(byte[] data, CancellationToken cancellationToken = default)
{
ArgumentNullException.ThrowIfNull(data);
ObjectDisposedException.ThrowIf(_disposed, this);

LogTransmitting(_logger, data.Length, FrequencyHz / 1_000_000, Modulation, BaudRate);

await _transport.SendCommandAsync(RfCatCommands.SendData, data, cancellationToken)
.ConfigureAwait(false);

LogTransmissionComplete(_logger);
}

/// <inheritdoc/>
public Task ResetAsync(CancellationToken cancellationToken = default)
{
ObjectDisposedException.ThrowIf(_disposed, this);
LogResetting(_logger);
return _transport.SendCommandAsync(RfCatCommands.Reset, cancellationToken: cancellationToken);
}

// ----- Private helpers -----

private Task SetFrequencyAsync(double frequencyHz, CancellationToken cancellationToken)
{
// The CC1111 frequency register is 3 bytes derived from: freq / (26e6 / 2^16)
var freqWord = (uint)(frequencyHz / (26_000_000.0 / 65536.0));
var payload = new byte[]
{
(byte)((freqWord >> 16) & 0xFF),
(byte)((freqWord >> 8) & 0xFF),
(byte)(freqWord & 0xFF),
};
return _transport.SendCommandAsync(RfCatCommands.SetFreq, payload, cancellationToken);
}

private Task SetModulationAsync(Modulation modulation, CancellationToken cancellationToken)
=> _transport.SendCommandAsync(RfCatCommands.SetModulation, [(byte)modulation], cancellationToken);

private Task SetBaudRateAsync(uint baudRate, CancellationToken cancellationToken)
{
var payload = BitConverter.GetBytes(baudRate);
if (BitConverter.IsLittleEndian)
Array.Reverse(payload);
return _transport.SendCommandAsync(RfCatCommands.SetBaudRate, payload, cancellationToken);
}

/// <inheritdoc/>
public void Dispose()
{
if (_disposed)
return;
_disposed = true;
_transport.Dispose();
}

[LoggerMessage(Level = LogLevel.Information, Message = "Configuring YARD Stick One: freq={FreqMHz:F4} MHz mod={Mod} baud={Baud}")]
private static partial void LogConfiguring(ILogger logger, double freqMHz, Modulation mod, uint baud);

[LoggerMessage(Level = LogLevel.Information, Message = "Starting receive at {FreqMHz:F4} MHz ({Mod}, {Baud} baud)...")]
private static partial void LogStartingReceive(ILogger logger, double freqMHz, Modulation mod, uint baud);

[LoggerMessage(Level = LogLevel.Information, Message = "Received {Bytes} bytes.")]
private static partial void LogReceived(ILogger logger, int bytes);

[LoggerMessage(Level = LogLevel.Information, Message = "Transmitting {Bytes} bytes at {FreqMHz:F4} MHz ({Mod}, {Baud} baud)...")]
private static partial void LogTransmitting(ILogger logger, int bytes, double freqMHz, Modulation mod, uint baud);

[LoggerMessage(Level = LogLevel.Information, Message = "Transmission complete.")]
private static partial void LogTransmissionComplete(ILogger logger);

[LoggerMessage(Level = LogLevel.Information, Message = "Resetting YARD Stick One...")]
private static partial void LogResetting(ILogger logger);
}