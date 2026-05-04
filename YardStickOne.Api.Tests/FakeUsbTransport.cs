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
	public bool IsDisposed { get; private set; }

	/// <summary>Enqueues a response to be returned by the next <see cref="ReadResponseAsync"/> call.</summary>
	public void EnqueueResponse(byte[] data) => _responses.Enqueue(data);

	/// <inheritdoc/>
	public Task SendCommandAsync(byte command, byte[]? payload = null, CancellationToken cancellationToken = default)
	{
		SentCommands.Add((command, payload));
		return Task.CompletedTask;
	}

	/// <inheritdoc/>
	public Task<byte[]> ReadResponseAsync(int maxLength = 64, CancellationToken cancellationToken = default)
	{
		if (_responses.TryDequeue(out var data))
			return Task.FromResult(data);

		return Task.FromResult(Array.Empty<byte>());
	}

	/// <inheritdoc/>
	public void Dispose() => IsDisposed = true;
}
