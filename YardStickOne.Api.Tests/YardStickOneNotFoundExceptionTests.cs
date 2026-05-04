namespace YardStickOne.Api.Tests;

public sealed class YardStickOneNotFoundExceptionTests
{
	[Fact]
	public void DefaultConstructor_HasMessage()
	{
		var ex = new YardStickOneNotFoundException();
		ex.Message.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public void StringConstructor_UsesSuppliedMessage()
	{
		var ex = new YardStickOneNotFoundException("custom message");
		ex.Message.Should().Be("custom message");
	}

	[Fact]
	public void InnerExceptionConstructor_ChainsException()
	{
		var inner = new InvalidOperationException("inner");
		var ex = new YardStickOneNotFoundException("outer", inner);
		ex.InnerException.Should().BeSameAs(inner);
	}
}
