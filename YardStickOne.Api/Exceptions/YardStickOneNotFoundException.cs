namespace YardStickOne.Api.Exceptions;

/// <summary>
/// Thrown when no YARD Stick One device can be found on the USB bus.
/// </summary>
public sealed class YardStickOneNotFoundException : Exception
{
	/// <inheritdoc/>
	public YardStickOneNotFoundException()
		: base("No YARD Stick One device was found. Ensure the device is plugged in and the driver is installed.")
	{
	}

	/// <inheritdoc/>
	public YardStickOneNotFoundException(string message)
		: base(message)
	{
	}

	/// <inheritdoc/>
	public YardStickOneNotFoundException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
