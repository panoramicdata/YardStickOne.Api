namespace YardStickOne.Api.Exceptions;

/// <summary>
/// Thrown when a USB command sent to the YARD Stick One fails.
/// </summary>
public sealed class YardStickOneCommandException : Exception
{
	/// <inheritdoc/>
	public YardStickOneCommandException()
		: base("A command to the YARD Stick One failed.")
	{
	}

	/// <inheritdoc/>
	public YardStickOneCommandException(string message)
		: base(message)
	{
	}

	/// <inheritdoc/>
	public YardStickOneCommandException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
