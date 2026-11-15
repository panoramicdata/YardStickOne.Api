using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace YardStickOne.Api.Tests;

/// <summary>
/// Scripted pipe double: queued chunks are returned by reads, writes are recorded.
/// </summary>
internal sealed class FakeWinUsbPipe : IWinUsbPipe
{
	private const int TimeoutError = 0x79;
	private readonly Queue<byte[]> _reads = new();

	public List<(byte Endpoint, byte[] Data)> Writes { get; } = [];

	public List<byte> ReadEndpoints { get; } = [];

	public int? WriteError { get; set; }

	public int? ReadError { get; set; }

	public uint? TransferredOverride { get; set; }

	public bool ThrowOnWrite { get; set; }

	public bool IsDisposed { get; private set; }

	public static byte[] Frame(byte app, byte cmd, params byte[] payload)
		=> [(byte)'@', app, cmd, (byte)(payload.Length & 0xFF), (byte)(payload.Length >> 8), .. payload];

	public void EnqueueRead(byte[] chunk) => _reads.Enqueue(chunk);

	public void EnqueueFrame(byte app, byte cmd, params byte[] payload)
		=> _reads.Enqueue(Frame(app, cmd, payload));

	public WinUsbTransport CreateTransport() => CreateTransport(NullLogger.Instance);

	public WinUsbTransport CreateTransport(ILogger logger) => new(this, 0x05, 0x85, logger);

	public bool Write(byte endpoint, byte[] buffer, out uint transferred, out int error)
	{
		if (ThrowOnWrite)
		{
			throw new InvalidOperationException("Write failed.");
		}

		Writes.Add((endpoint, [.. buffer]));
		if (WriteError is { } writeError)
		{
			transferred = 0;
			error = writeError;
			return false;
		}

		transferred = TransferredOverride ?? (uint)buffer.Length;
		error = 0;
		return true;
	}

	public bool Read(byte endpoint, byte[] buffer, out uint transferred, out int error)
	{
		ReadEndpoints.Add(endpoint);
		transferred = 0;
		if (ReadError is { } readError)
		{
			error = readError;
			return false;
		}

		if (!_reads.TryDequeue(out var chunk))
		{
			error = TimeoutError;
			return false;
		}

		var count = Math.Min(chunk.Length, buffer.Length);
		Array.Copy(chunk, buffer, count);
		transferred = (uint)count;
		error = 0;
		return true;
	}

	public void Dispose() => IsDisposed = true;
}
