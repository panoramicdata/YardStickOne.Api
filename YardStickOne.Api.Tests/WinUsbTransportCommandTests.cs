using System.Text;

namespace YardStickOne.Api.Tests;

public sealed class WinUsbTransportCommandTests
{
	private const byte SystemApp = 0xFF;
	private const byte Nic = 0x42;

	[Theory]
	[InlineData(RfCatCommands.RxMode, 0x02)]
	[InlineData(RfCatCommands.TxMode, 0x03)]
	[InlineData(RfCatCommands.IdleMode, 0x04)]
	public async Task ModeCommand_WritesRfModeStrobe(byte command, byte strobe)
	{
		var pipe = new FakeWinUsbPipe();
		pipe.EnqueueFrame(SystemApp, 0x88);
		using var transport = pipe.CreateTransport();

		await transport.SendCommandAsync(command);

		var write = pipe.Writes.Should().ContainSingle().Subject;
		write.Endpoint.Should().Be(0x05);
		write.Data.Should().Equal(SystemApp, 0x88, 1, 0, strobe);
	}

	[Fact]
	public async Task Reset_WritesResetPayloadAndDoesNotWaitForReply()
	{
		var pipe = new FakeWinUsbPipe();
		using var transport = pipe.CreateTransport();

		await transport.SendCommandAsync(RfCatCommands.Reset);

		var data = pipe.Writes.Should().ContainSingle().Subject.Data;
		data[..4].Should().Equal(SystemApp, 0x8F, 10, 0);
		data[4..].Should().Equal(Encoding.ASCII.GetBytes("RESET_NOW\0"));
	}

	[Theory]
	[InlineData(null, "PING")]
	[InlineData(new byte[] { 0x01, 0x02 }, "\u0001\u0002")]
	public async Task Ping_EchoesPayloadOrDefaultsToPing(byte[]? payload, string expected)
	{
		var pipe = new FakeWinUsbPipe();
		pipe.EnqueueFrame(SystemApp, 0x82);
		using var transport = pipe.CreateTransport();

		await transport.SendCommandAsync(RfCatCommands.Ping, payload);

		var data = pipe.Writes.Should().ContainSingle().Subject.Data;
		data[..4].Should().Equal(SystemApp, 0x82, (byte)expected.Length, 0);
		data[4..].Should().Equal(Encoding.ASCII.GetBytes(expected));
	}

	[Fact]
	public async Task SetFrequency_PokesThreeByteFrequencyBlock()
	{
		var pipe = new FakeWinUsbPipe();
		pipe.EnqueueFrame(SystemApp, 0x81);
		using var transport = pipe.CreateTransport();

		await transport.SendCommandAsync(RfCatCommands.SetFreq, [0x10, 0xB0, 0x70]);

		pipe.Writes.Should().ContainSingle().Subject.Data
			.Should().Equal(SystemApp, 0x81, 5, 0, 0x0D, 0xDF, 0x10, 0xB0, 0x70);
	}

	[Theory]
	[InlineData(RfCatCommands.SetModulation, 0x30, 0x12, 0x04, 0x34)]
	[InlineData(RfCatCommands.SetModulation, 0x31, 0x12, 0x04, 0x34)]
	[InlineData(RfCatCommands.SetModulation, 0x70, 0x12, 0xFF, 0xFF)]
	[InlineData(RfCatCommands.SetModulation, 0x00, 0x12, 0x7F, 0x0F)]
	[InlineData(RfCatCommands.SetSyncMode, 0x00, 0x12, 0xFF, 0xF8)]
	[InlineData(RfCatCommands.SetSyncMode, 0x0B, 0x12, 0x00, 0x03)]
	[InlineData(RfCatCommands.SetInfinitePkt, 0x00, 0x08, 0xFF, 0xFE)]
	[InlineData(RfCatCommands.SetInfinitePkt, 0x00, 0x08, 0x00, 0x02)]
	public async Task RegisterCommand_ReadsThenWritesMaskedRegister(byte command, byte argument, byte addressLow, byte current, byte expected)
	{
		var pipe = new FakeWinUsbPipe();
		pipe.EnqueueFrame(SystemApp, 0x80, current);
		pipe.EnqueueFrame(SystemApp, 0x81);
		using var transport = pipe.CreateTransport();

		await transport.SendCommandAsync(command, [argument]);

		pipe.Writes.Should().HaveCount(2);
		pipe.Writes[0].Data.Should().Equal(SystemApp, 0x80, 4, 0, 1, 0, addressLow, 0xDF);
		pipe.Writes[1].Data.Should().Equal(SystemApp, 0x81, 3, 0, addressLow, 0xDF, expected);
	}

	[Fact]
	public async Task EmptyRegisterReply_IsTreatedAsZero()
	{
		var pipe = new FakeWinUsbPipe();
		pipe.EnqueueFrame(SystemApp, 0x80);
		pipe.EnqueueFrame(SystemApp, 0x81);
		using var transport = pipe.CreateTransport();

		await transport.SendCommandAsync(RfCatCommands.SetInfinitePkt);

		pipe.Writes[1].Data[^1].Should().Be(0x02);
	}

	[Fact]
	public async Task SetBaudRate_WritesDataRateMantissaThenExponent()
	{
		var pipe = new FakeWinUsbPipe();
		pipe.EnqueueFrame(SystemApp, 0x80, 0xAB);
		pipe.EnqueueFrame(SystemApp, 0x81);
		pipe.EnqueueFrame(SystemApp, 0x81);
		using var transport = pipe.CreateTransport();

		await transport.SendCommandAsync(RfCatCommands.SetBaudRate, [0x00, 0x00, 0x12, 0xC0]);

		pipe.Writes.Should().HaveCount(3);
		pipe.Writes[1].Data.Should().Equal(SystemApp, 0x81, 3, 0, 0x11, 0xDF, 163);
		pipe.Writes[2].Data.Should().Equal(SystemApp, 0x81, 3, 0, 0x10, 0xDF, 0xA7);
	}

	[Theory]
	[InlineData(RfCatCommands.SetFreq, new byte[] { 1, 2, 3, 4, 5 }, "SetFreq expects 3-byte payload.")]
	[InlineData(RfCatCommands.SetModulation, new byte[] { 1, 2 }, "SetModulation expects 1-byte payload.")]
	[InlineData(RfCatCommands.SetBaudRate, new byte[] { 1, 2, 3 }, "SetBaudRate expects 4-byte payload.")]
	[InlineData(RfCatCommands.SetSyncMode, new byte[] { 1, 2 }, "SetSyncMode expects 1-byte payload.")]
	[InlineData(RfCatCommands.SetBaudRate, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, "cannot be represented")]
	[InlineData(RfCatCommands.SetBaudRate, new byte[] { 0, 0, 0, 0 }, "cannot be represented")]
	[InlineData(RfCatCommands.GetSerial, new byte[] { 1 }, "Unsupported rfcat command byte 0x73.")]
	[InlineData(RfCatCommands.SetSyncWord, new byte[] { 1 }, "Unsupported rfcat command byte 0x33.")]
	public async Task InvalidCommand_ThrowsWithoutWriting(byte command, byte[] payload, string expectedMessage)
	{
		var pipe = new FakeWinUsbPipe();
		using var transport = pipe.CreateTransport();

		await transport.Invoking(t => t.SendCommandAsync(command, payload))
			.Should().ThrowAsync<YardStickOneCommandException>().WithMessage($"*{expectedMessage}*");

		pipe.Writes.Should().BeEmpty();
	}

	[Fact]
	public async Task SendData_PrefixesLengthHeaderToPayload()
	{
		var pipe = new FakeWinUsbPipe();
		pipe.EnqueueFrame(Nic, 0x02);
		using var transport = pipe.CreateTransport();

		await transport.SendCommandAsync(RfCatCommands.SendData, [0xAA, 0x55]);

		pipe.Writes.Should().ContainSingle().Subject.Data
			.Should().Equal(Nic, 0x02, 8, 0, 2, 0, 0, 0, 0, 0, 0xAA, 0x55);
	}

	[Theory]
	[InlineData(64, new byte[] { 1, 2, 3, 4 })]
	[InlineData(2, new byte[] { 1, 2 })]
	public async Task RecvData_StoresPayloadForNextRead(int maxLength, byte[] expected)
	{
		var pipe = new FakeWinUsbPipe();
		pipe.EnqueueFrame(Nic, 0x01, 1, 2, 3, 4);
		using var transport = pipe.CreateTransport();

		await transport.SendCommandAsync(RfCatCommands.RecvData);

		(await transport.ReadResponseAsync(maxLength)).Should().Equal(expected);
		(await transport.ReadResponseAsync(maxLength)).Should().BeEmpty();
	}

	[Fact]
	public async Task RecvData_WithNoReply_LeavesNothingToRead()
	{
		var pipe = new FakeWinUsbPipe();
		using var transport = pipe.CreateTransport();

		await transport.SendCommandAsync(RfCatCommands.RecvData);

		(await transport.ReadResponseAsync()).Should().BeEmpty();
	}
}
