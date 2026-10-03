namespace YardStickOne.Web.Services;

/// <summary>
/// Thread-safe circular buffer holding the most recent <see cref="Capacity"/> bytes of RF samples.
/// Supports reading a contiguous snapshot without locking via an epoch-copy mechanism.
/// </summary>
internal sealed class RingBuffer
{
	private readonly byte[] _buf;
	private long _totalWritten; // monotonically increasing; never wraps

	/// <summary>Maximum number of samples the buffer can hold.</summary>
	public int Capacity => _buf.Length;

	/// <summary>Total samples written since the buffer was created.</summary>
	public long TotalWritten => Volatile.Read(ref _totalWritten);

	public RingBuffer(int capacity = 200_000)
	{
		_buf = new byte[capacity];
	}

	/// <summary>
	/// Appends <paramref name="data"/> to the ring buffer, overwriting the oldest entries when full.
	/// </summary>
	public void Write(ReadOnlySpan<byte> data)
	{
		if (data.IsEmpty) return;

		lock (_buf)
		{
			foreach (var b in data)
				_buf[_totalWritten++ % Capacity] = b;
		}
	}

	/// <summary>
	/// Copies the most recent <paramref name="count"/> samples into <paramref name="dest"/>.
	/// Returns the actual number of samples copied (may be less than <paramref name="count"/>
	/// if fewer samples have been written).
	/// </summary>
	public int ReadLatest(Span<byte> dest, int count)
	{
		lock (_buf)
		{
			var available = (int)Math.Min(_totalWritten, Math.Min(count, Capacity));
			var start = _totalWritten - available;
			for (var i = 0; i < available; i++)
				dest[i] = _buf[(start + i) % Capacity];
			return available;
		}
	}

	/// <summary>
	/// Returns a newly allocated array containing all samples currently in the buffer,
	/// ordered oldest-first.
	/// </summary>
	public byte[] Snapshot()
	{
		lock (_buf)
		{
			var count = (int)Math.Min(_totalWritten, Capacity);
			var result = new byte[count];
			var start = _totalWritten - count;
			for (var i = 0; i < count; i++)
				result[i] = _buf[(start + i) % Capacity];
			return result;
		}
	}
}
