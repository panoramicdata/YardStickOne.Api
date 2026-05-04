using Microsoft.Extensions.Logging.Abstractions;

namespace YardStickOne.Api.Tests;

public sealed class YardStickOneClientOptionsTests
{
	[Fact]
	public void DefaultFrequency_Is433_92MHz()
	{
		var options = new YardStickOneClientOptions();
		options.DefaultFrequencyHz.Should().Be(433_920_000);
	}

	[Fact]
	public void DefaultModulation_IsAskOok()
	{
		var options = new YardStickOneClientOptions();
		options.DefaultModulation.Should().Be(Modulation.AskOok);
	}

	[Fact]
	public void DefaultBaudRate_Is4800()
	{
		var options = new YardStickOneClientOptions();
		options.DefaultBaudRate.Should().Be(4800u);
	}

	[Fact]
	public void DefaultVendorId_IsYardStickOne()
	{
		var options = new YardStickOneClientOptions();
		options.VendorId.Should().Be(0x1D50);
	}

	[Fact]
	public void DefaultProductId_IsYardStickOne()
	{
		var options = new YardStickOneClientOptions();
		options.ProductId.Should().Be(0x605B);
	}
}
