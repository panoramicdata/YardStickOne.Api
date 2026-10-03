using Microsoft.AspNetCore.SignalR;
using YardStickOne.Api;
using YardStickOne.Api.Models;
using YardStickOne.Web.Hubs;

namespace YardStickOne.Web.Services;

/// <summary>
/// Singleton service that owns the <see cref="YardStickOneClient"/>, manages capture state,
/// maintains a 30-second ring buffer, and pushes live chunks to the oscilloscope hub.
/// </summary>
public sealed partial class CaptureService : IAsyncDisposable
{
	private readonly IHubContext<OscilloscopeHub> _hub;
	private readonly ILogger<CaptureService> _logger;
	private readonly ILoggerFactory _loggerFactory;
	private readonly RingBuffer _ringBuffer = new(capacity: 200_000);

	private YardStickOneClient? _client;
	private CancellationTokenSource? _captureCts;
	private Task? _captureTask;
	private readonly object _lock = new();

	// Current radio configuration
	private double _frequencyHz  = 433_920_000;
	private Modulation _modulation = Modulation.AskOok;
	private uint _baudRate         = 4800;

	public bool IsCapturing { get; private set; }
	public double FrequencyHz  => _frequencyHz;
	public Modulation Modulation => _modulation;
	public uint BaudRate        => _baudRate;
	public event Action<string>? CaptureFaulted;

	/// <summary>Returns a snapshot of all buffered samples (up to 30 s worth).</summary>
	public byte[] GetSnapshot() => _ringBuffer.Snapshot();

	public CaptureService(IHubContext<OscilloscopeHub> hub, ILoggerFactory loggerFactory)
	{
		_hub          = hub;
		_loggerFactory = loggerFactory;
		_logger       = loggerFactory.CreateLogger<CaptureService>();
	}

	/// <summary>
	/// Opens the YARD Stick One and starts continuous streaming at the given settings.
	/// </summary>
	public async Task StartAsync(
		double frequencyHz,
		Modulation modulation,
		uint baudRate,
		CancellationToken cancellationToken = default)
	{
		lock (_lock)
		{
			if (IsCapturing) return;
			IsCapturing = true;
		}

		_frequencyHz = frequencyHz;
		_modulation  = modulation;
		_baudRate    = baudRate;

		try
		{
			_client = new YardStickOneClient(
				new YardStickOneClientOptions
				{
					DefaultFrequencyHz = frequencyHz,
					DefaultBaudRate    = baudRate,
				},
				_loggerFactory.CreateLogger<YardStickOneClient>());

			await _client.ConfigureAsync(frequencyHz, modulation, baudRate, cancellationToken);

			_captureCts  = new CancellationTokenSource();
			_captureTask = RunCaptureLoopAsync(_captureCts.Token);
		}
		catch
		{
			lock (_lock) { IsCapturing = false; }
			_client?.Dispose();
			_client = null;
			throw;
		}
	}

	/// <summary>Stops the stream and returns the radio to idle.</summary>
	public async Task StopAsync()
	{
		CancellationTokenSource? cts;
		Task? task;

		lock (_lock)
		{
			if (!IsCapturing) return;
			cts  = _captureCts;
			task = _captureTask;
			_captureCts  = null;
			_captureTask = null;
			IsCapturing  = false;
		}

		if (cts is not null)
		{
			await cts.CancelAsync();
			if (task is not null)
				await task.ConfigureAwait(false);
			cts.Dispose();
		}

		_client?.Dispose();
		_client = null;
	}

	/// <summary>Transmits <paramref name="data"/> using the current radio configuration.</summary>
	public async Task TransmitAsync(byte[] data, CancellationToken cancellationToken = default)
	{
		if (_client is null)
			throw new InvalidOperationException("Device not open.");
		await _client.TransmitAsync(data, cancellationToken);
	}

	// ── private ──────────────────────────────────────────────────────────────

	private async Task RunCaptureLoopAsync(CancellationToken ct)
	{
		if (_client is null) return;

		try
		{
			await foreach (var signal in _client.StreamAsync(ct))
			{
				_ringBuffer.Write(signal.Data);

				// Push the raw chunk to all connected oscilloscope clients.
				await _hub.Clients.All
					.SendAsync("ChunkReceived", signal.Data, ct)
					.ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
			// expected on stop
		}
		catch (Exception ex)
		{
			lock (_lock)
			{
				IsCapturing = false;
				_captureCts = null;
				_captureTask = null;
			}

			_client?.Dispose();
			_client = null;

			LogCaptureLoopFailed(_logger, ex);
			CaptureFaulted?.Invoke(ex.Message);
		}
	}

	public async ValueTask DisposeAsync()
	{
		await StopAsync();
	}
}

public partial class CaptureService
{
	[LoggerMessage(Level = LogLevel.Error, Message = "Capture loop failed.")]
	private static partial void LogCaptureLoopFailed(ILogger logger, Exception ex);
}
