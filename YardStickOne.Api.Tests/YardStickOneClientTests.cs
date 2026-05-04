using Microsoft.Extensions.Logging.Abstractions;

namespace YardStickOne.Api.Tests;

public sealed class YardStickOneClientTests
{
	private static YardStickOneClient CreateClient(FakeUsbTransport transport)
	{
		var options = new YardStickOneClientOptions();
		return new YardStickOneClient(options, NullLogger<YardStickOneClient>.Instance, transport);
	}

	[Fact]
	public void Constructor_SetsDefaultsFromOptions()
	{
		using var transport = new FakeUsbTransport();
		using var client = CreateClient(transport);

		client.FrequencyHz.Should().Be(433_920_000);
		client.Modulation.Should().Be(Modulation.AskOok);
		client.BaudRate.Should().Be(4800u);
	}

	[Fact]
	public async Task ConfigureAsync_UpdatesProperties()
	{
		using var transport = new FakeUsbTransport();
		using var client = CreateClient(transport);

		await client.ConfigureAsync(868_000_000, Modulation.GFSK, 9600);

		client.FrequencyHz.Should().Be(868_000_000);
		client.Modulation.Should().Be(Modulation.GFSK);
		client.BaudRate.Should().Be(9600u);
	}

	[Fact]
	public async Task ConfigureAsync_SendsThreeCommands()
	{
		using var transport = new FakeUsbTransport();
		using var client = CreateClient(transport);

		await client.ConfigureAsync(433_920_000, Modulation.AskOok, 4800);

		// Expect SetFreq, SetModulation, SetBaudRate
		transport.SentCommands.Should().HaveCount(3);
		transport.SentCommands[0].Command.Should().Be(RfCatCommands.SetFreq);
		transport.SentCommands[1].Command.Should().Be(RfCatCommands.SetModulation);
		transport.SentCommands[2].Command.Should().Be(RfCatCommands.SetBaudRate);
	}

	[Fact]
	public async Task TransmitAsync_SendsDataCommand()
	{
		using var transport = new FakeUsbTransport();
		using var client = CreateClient(transport);

		var data = new byte[] { 0xAA, 0x55, 0xAA };
		await client.TransmitAsync(data);

		transport.SentCommands.Should().ContainSingle(c => c.Command == RfCatCommands.SendData);
		transport.SentCommands[0].Payload.Should().BeEquivalentTo(data);
	}

	[Fact]
	public async Task ReceiveAsync_EntersRxThenIdle()
	{
		using var transport = new FakeUsbTransport();
		transport.EnqueueResponse([0x01, 0x02, 0x03]);
		using var client = CreateClient(transport);

		var signal = await client.ReceiveAsync();

		signal.Data.Should().BeEquivalentTo(new byte[] { 0x01, 0x02, 0x03 });
		transport.SentCommands[0].Command.Should().Be(RfCatCommands.RxMode);
		transport.SentCommands[1].Command.Should().Be(RfCatCommands.IdleMode);
	}

	[Fact]
	public async Task ResetAsync_SendsResetCommand()
	{
		using var transport = new FakeUsbTransport();
		using var client = CreateClient(transport);

		await client.ResetAsync();

		transport.SentCommands.Should().ContainSingle(c => c.Command == RfCatCommands.Reset);
	}

	[Fact]
	public void Dispose_DisposesTransport()
	{
		var transport = new FakeUsbTransport();
		var client = CreateClient(transport);

		client.Dispose();

		transport.IsDisposed.Should().BeTrue();
	}

	[Fact]
	public async Task TransmitAsync_AfterDispose_Throws()
	{
		using var transport = new FakeUsbTransport();
		var client = CreateClient(transport);
		client.Dispose();

		await client.Invoking(c => c.TransmitAsync([0x01]))
			.Should().ThrowAsync<ObjectDisposedException>();
	}

	[Fact]
	public async Task TransmitAsync_NullData_Throws()
	{
		using var transport = new FakeUsbTransport();
		using var client = CreateClient(transport);

		await client.Invoking(c => c.TransmitAsync((byte[])null!))
			.Should().ThrowAsync<ArgumentNullException>();
	}
}
