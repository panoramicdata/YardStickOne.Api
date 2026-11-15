using Microsoft.Extensions.Logging.Abstractions;

namespace YardStickOne.Api.Tests;

/// <summary>
/// In-memory transport double that captures sent commands and allows pre-queuing responses.
/// </summary>
internal sealed class FakeUsbTransport : IUsbTransport
{
	private readonly Queue<byte[]> _responses = new();

	/// <summary>All commands sent via <see cref="SendCommandAsync"/>.</summary>
	public List<(byte Command, byte[]? Payload)> SentCommands { get; } = [];

	/// <summary>Whether <see cref="Dispose"/> has been called.</summary>
	public bool IsDisposed => DisposeCount > 0;

	/// <summary>How many times <see cref="Dispose"/> has been called.</summary>
	public int DisposeCount { get; private set; }

	/// <summary>When set, returns the exception a sent command should fail with, or null to succeed.</summary>
	public Func<byte, Exception?>? FailureFor { get; set; }

	/// <summary>Enqueues a response to be returned by the next <see cref="ReadResponseAsync"/> call.</summary>
	public void EnqueueResponse(byte[] data) => _responses.Enqueue(data);

	/// <inheritdoc/>
	public Task SendCommandAsync(byte command, byte[]? payload = null, CancellationToken cancellationToken = default)
	{
		SentCommands.Add((command, payload));
		var failure = FailureFor?.Invoke(command);
		return failure is null ? Task.CompletedTask : Task.FromException(failure);
	}

	/// <inheritdoc/>
	public Task<byte[]> ReadResponseAsync(int maxLength = 64, CancellationToken cancellationToken = default)
	{
		if (_responses.TryDequeue(out var data))
		{
			return Task.FromResult(data);
		}

		return Task.FromResult(Array.Empty<byte>());
	}

	/// <inheritdoc/>
	public Task<byte[]> PollIncomingFrameAsync(CancellationToken cancellationToken = default)
	{
		if (_responses.TryDequeue(out var data))
		{
			return Task.FromResult(data);
		}

		return Task.FromResult(Array.Empty<byte>());
	}

	/// <inheritdoc/>
	public void Dispose() => DisposeCount++;
}
