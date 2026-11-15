namespace YardStickOne.Api.Tests;

public sealed class YardStickOneCommandExceptionTests
{
	[Fact]
	public void DefaultConstructor_HasDescriptiveMessage()
	{
		var exception = new YardStickOneCommandException();

		exception.Message.Should().Be("A command to the YARD Stick One failed.");
	}

	[Fact]
	public void MessageConstructor_KeepsMessage()
	{
		var exception = new YardStickOneCommandException("boom");

		exception.Message.Should().Be("boom");
		exception.InnerException.Should().BeNull();
	}

	[Fact]
	public void InnerExceptionConstructor_KeepsBoth()
	{
		var inner = new InvalidOperationException("inner");

		var exception = new YardStickOneCommandException("boom", inner);

		exception.Message.Should().Be("boom");
		exception.InnerException.Should().BeSameAs(inner);
	}
}
