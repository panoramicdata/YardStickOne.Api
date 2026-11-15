namespace YardStickOne.Api.Tests;

public sealed class WinUsbTransportFramingTests
{
	private const byte SystemApp = 0xFF;
	private const byte Nic = 0x42;

	[Fact]
	public async Task Poll_ReturnsNicRecvPayload()
	{
		var pipe = new FakeWinUsbPipe();
		pipe.EnqueueFrame(Nic, 0x01, 9, 8, 7);
		using var transport = pipe.CreateTransport();

		(await transport.PollIncomingFrameAsync()).Should().Equal(9, 8, 7);
		pipe.ReadEndpoints.Should().Equal(0x85);
	}

	[Fact]
	public async Task Poll_SkipsLeadingNoiseBeforeStartOfFrame()
	{
		var pipe = new FakeWinUsbPipe();
		pipe.EnqueueRead([0x00, 0x01, .. FakeWinUsbPipe.Frame(Nic, 0x01, 5)]);
		using var transport = pipe.CreateTransport();

		(await transport.PollIncomingFrameAsync()).Should().Equal(5);
	}

	[Fact]
	public async Task Poll_AssemblesFrameSplitAcrossReads()
	{
		var frame = FakeWinUsbPipe.Frame(Nic, 0x01, 1, 2, 3);
		var pipe = new FakeWinUsbPipe();
		pipe.EnqueueRead(frame[..4]);
		pipe.EnqueueRead(frame[4..]);
		using var transport = pipe.CreateTransport();

		(await transport.PollIncomingFrameAsync()).Should().BeEmpty();
		(await transport.PollIncomingFrameAsync()).Should().Equal(1, 2, 3);
	}

	[Fact]
	public async Task Poll_DiscardsNonNicFramesAndReturnsNothing()
	{
		var pipe = new FakeWinUsbPipe();
		pipe.EnqueueFrame(SystemApp, 0x83, 1);
		using var transport = pipe.CreateTransport();

		(await transport.PollIncomingFrameAsync()).Should().BeEmpty();
	}

	[Fact]
	public async Task Poll_WithNothingToRead_ReturnsEmpty()
	{
		var pipe = new FakeWinUsbPipe();
		using var transport = pipe.CreateTransport();

		(await transport.PollIncomingFrameAsync()).Should().BeEmpty();
	}

	[Fact]
	public async Task Poll_RecoversAfterLongRunOfNoise()
	{
		var pipe = new FakeWinUsbPipe();
		for (var i = 0; i < 3; i++)
		{
			pipe.EnqueueRead(new byte[512]);
		}

		pipe.EnqueueFrame(Nic, 0x01, 4);
		using var transport = pipe.CreateTransport();

		for (var i = 0; i < 3; i++)
		{
			(await transport.PollIncomingFrameAsync()).Should().BeEmpty();
		}

		(await transport.PollIncomingFrameAsync()).Should().Equal(4);
	}

	[Fact]
	public async Task Command_SkipsUnrelatedFramesWhileWaitingForReply()
	{
		var pipe = new FakeWinUsbPipe();
		pipe.EnqueueFrame(Nic, 0x01, 9);
		pipe.EnqueueRead([.. FakeWinUsbPipe.Frame(SystemApp, 0x83), .. FakeWinUsbPipe.Frame(SystemApp, 0x82)]);
		using var transport = pipe.CreateTransport();

		await transport.Invoking(t => t.SendCommandAsync(RfCatCommands.Ping)).Should().NotThrowAsync();
	}

	[Theory]
	[InlineData(5, null, "USB write failed: Win32 0x00000005")]
	[InlineData(null, 1u, "USB write incomplete: sent 1/")]
	public async Task Write_FailureSurfacesAsCommandException(int? writeError, uint? transferred, string expectedMessage)
	{
		var pipe = new FakeWinUsbPipe { WriteError = writeError, TransferredOverride = transferred };
		using var transport = pipe.CreateTransport();

		await transport.Invoking(t => t.SendCommandAsync(RfCatCommands.Ping))
			.Should().ThrowAsync<YardStickOneCommandException>().WithMessage($"{expectedMessage}*");
	}

	[Fact]
	public async Task Read_HardErrorSurfacesAsCommandException()
	{
		var pipe = new FakeWinUsbPipe { ReadError = 5 };
		using var transport = pipe.CreateTransport();

		await transport.Invoking(t => t.SendCommandAsync(RfCatCommands.Ping))
			.Should().ThrowAsync<YardStickOneCommandException>().WithMessage("USB read failed: Win32 0x00000005");
	}

	[Fact]
	public async Task Command_WithoutReply_TimesOutNamingTheExpectedFrame()
	{
		var pipe = new FakeWinUsbPipe();
		using var transport = pipe.CreateTransport();

		await transport.Invoking(t => t.SendCommandAsync(RfCatCommands.Ping))
			.Should().ThrowAsync<YardStickOneCommandException>()
			.WithMessage("Timeout waiting for response app=0xFF cmd=0x82.");
	}

	[Fact]
	public async Task DebugLogging_RecordsOutgoingAndIncomingBytes()
	{
		var logger = new ListLogger();
		var pipe = new FakeWinUsbPipe();
		pipe.EnqueueFrame(SystemApp, 0x82);
		using var transport = pipe.CreateTransport(logger);

		await transport.SendCommandAsync(RfCatCommands.Ping);

		logger.Messages.Should().Contain(m => m.StartsWith("Debug: USB OUT cmd=0x82", StringComparison.Ordinal));
		logger.Messages.Should().Contain(m => m.StartsWith("Debug: USB IN", StringComparison.Ordinal));
	}

	[Fact]
	public async Task DebugLogging_RecordsReadTimeout()
	{
		var logger = new ListLogger();
		var pipe = new FakeWinUsbPipe();
		using var transport = pipe.CreateTransport(logger);

		await transport.PollIncomingFrameAsync();

		logger.Messages.Should().ContainSingle().Which.Should().StartWith("Debug: USB IN  len=0");
	}

	[Fact]
	public void Dispose_SendsIdleThenReleasesPipeOnce()
	{
		var pipe = new FakeWinUsbPipe();
		var transport = pipe.CreateTransport();

		transport.Dispose();
		transport.Dispose();

		pipe.Writes.Should().ContainSingle().Subject.Data.Should().Equal(SystemApp, 0x88, 1, 0, 0x04);
		pipe.ReadEndpoints.Should().Equal(0x85);
		pipe.IsDisposed.Should().BeTrue();
	}

	[Fact]
	public void Dispose_SwallowsFailureToSendIdle()
	{
		var pipe = new FakeWinUsbPipe { ThrowOnWrite = true };
		var transport = pipe.CreateTransport();

		transport.Invoking(t => t.Dispose()).Should().NotThrow();

		pipe.IsDisposed.Should().BeTrue();
	}

	[Fact]
	public async Task Operations_AfterDispose_Throw()
	{
		var transport = new FakeWinUsbPipe().CreateTransport();
		transport.Dispose();
		Func<Task>[] operations =
		[
			() => transport.SendCommandAsync(RfCatCommands.Ping),
			async () => await transport.ReadResponseAsync(),
			async () => await transport.PollIncomingFrameAsync(),
		];

		foreach (var operation in operations)
		{
			await operation.Should().ThrowAsync<ObjectDisposedException>();
		}
	}
}
