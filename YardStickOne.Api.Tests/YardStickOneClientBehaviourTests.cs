using Microsoft.Extensions.Logging.Abstractions;

namespace YardStickOne.Api.Tests;

public sealed class YardStickOneClientBehaviourTests
{
	private const string PingTimeoutMessage = "Timeout waiting for response app=0xFF cmd=0x82.";

	private static YardStickOneClient CreateClient(FakeUsbTransport transport, YardStickOneClientOptions? options = null)
		=> new(options ?? new YardStickOneClientOptions(), NullLogger<YardStickOneClient>.Instance, transport);

	private static Func<byte, Exception?> FailPingTimes(int times, string message)
	{
		var remaining = times;
		return command => command == RfCatCommands.Ping && remaining-- > 0
			? new YardStickOneCommandException(message)
			: null;
	}

	[Fact]
	public async Task ConfigureAsync_SendsEncodedRadioSettings()
	{
		using var transport = new FakeUsbTransport();
		using var client = CreateClient(transport);

		await client.ConfigureAsync(433_920_000, Modulation.AskOok, 4800);

		var commands = transport.SentCommands;
		commands[1].Payload.Should().Equal(0x10, 0xB0, 0x71);
		commands[2].Payload.Should().Equal((byte)Modulation.AskOok);
		commands[3].Payload.Should().Equal(0x00, 0x00, 0x12, 0xC0);
		commands[4].Payload.Should().Equal(0x00);
		commands[5].Payload.Should().BeNull();
	}

	[Theory]
	[InlineData(1, 2, true)]
	[InlineData(2, 3, true)]
	[InlineData(3, 3, false)]
	public async Task ConfigureAsync_RetriesPingTimeoutsUpToThreeAttempts(int failures, int expectedPings, bool succeeds)
	{
		using var transport = new FakeUsbTransport { FailureFor = FailPingTimes(failures, PingTimeoutMessage) };
		using var client = CreateClient(transport);

		var configure = client.Invoking(c => c.ConfigureAsync(868_000_000, Modulation.GFSK, 9600));

		if (succeeds)
		{
			await configure.Should().NotThrowAsync();
		}
		else
		{
			await configure.Should().ThrowAsync<YardStickOneCommandException>();
		}

		transport.SentCommands.Count(c => c.Command == RfCatCommands.Ping).Should().Be(expectedPings);
		transport.SentCommands.Count(c => c.Command == RfCatCommands.IdleMode).Should().Be(expectedPings - 1);
	}

	[Fact]
	public async Task ConfigureAsync_DoesNotRetryOtherPingFailures()
	{
		using var transport = new FakeUsbTransport { FailureFor = FailPingTimes(1, "USB write failed") };
		using var client = CreateClient(transport);

		await client.Invoking(c => c.ConfigureAsync(868_000_000, Modulation.GFSK, 9600))
			.Should().ThrowAsync<YardStickOneCommandException>().WithMessage("USB write failed");

		transport.SentCommands.Should().ContainSingle();
		client.FrequencyHz.Should().Be(433_920_000);
	}

	[Fact]
	public async Task ConfigureAsync_ContinuesWhenIdleRecoveryFails()
	{
		using var transport = new FakeUsbTransport();
		var pingFailure = FailPingTimes(1, PingTimeoutMessage);
		transport.FailureFor = command => command == RfCatCommands.IdleMode
			? new YardStickOneCommandException("idle failed")
			: pingFailure(command);
		using var client = CreateClient(transport);

		await client.ConfigureAsync(868_000_000, Modulation.GFSK, 9600);

		client.FrequencyHz.Should().Be(868_000_000);
	}

	[Fact]
	public async Task TransmitAsync_Signal_WithMatchingSettings_OnlySendsData()
	{
		using var transport = new FakeUsbTransport();
		using var client = CreateClient(transport);
		var signal = new RfSignal
		{
			FrequencyHz = 433_920_000,
			Modulation = Modulation.AskOok,
			BaudRate = 4800,
			Data = [0x01, 0x02],
		};

		await client.TransmitAsync(signal);

		transport.SentCommands.Should().ContainSingle().Which.Command.Should().Be(RfCatCommands.SendData);
	}

	[Fact]
	public async Task TransmitAsync_Signal_WithDifferentSettings_ReconfiguresFirst()
	{
		using var transport = new FakeUsbTransport();
		using var client = CreateClient(transport);
		var signal = new RfSignal
		{
			FrequencyHz = 315_000_000,
			Modulation = Modulation.FSK2,
			BaudRate = 2400,
			Data = [0x0A],
		};

		await client.TransmitAsync(signal);

		transport.SentCommands.Should().HaveCount(7);
		transport.SentCommands[^1].Command.Should().Be(RfCatCommands.SendData);
		client.FrequencyHz.Should().Be(315_000_000);
		client.Modulation.Should().Be(Modulation.FSK2);
		client.BaudRate.Should().Be(2400u);
	}

	[Fact]
	public async Task TransmitAsync_NullSignal_Throws()
	{
		using var transport = new FakeUsbTransport();
		using var client = CreateClient(transport);

		await client.Invoking(c => c.TransmitAsync((RfSignal)null!))
			.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public async Task ReceiveAsync_SkipsStatusOnlyResponsesAndReportsCurrentSettings()
	{
		using var transport = new FakeUsbTransport();
		transport.EnqueueResponse([0x00]);
		transport.EnqueueResponse([0x0A, 0x0B]);
		using var client = CreateClient(transport);
		await client.ConfigureAsync(868_000_000, Modulation.GFSK, 9600);
		transport.SentCommands.Clear();

		var signal = await client.ReceiveAsync();

		signal.Data.Should().Equal(0x0A, 0x0B);
		signal.FrequencyHz.Should().Be(868_000_000);
		signal.Modulation.Should().Be(Modulation.GFSK);
		signal.BaudRate.Should().Be(9600u);
		transport.SentCommands.Count(c => c.Command == RfCatCommands.RecvData).Should().Be(2);
	}

	[Fact]
	public async Task ReceiveAsync_WithNoData_TimesOut()
	{
		using var transport = new FakeUsbTransport();
		using var client = CreateClient(transport, new YardStickOneClientOptions { ReceiveTimeout = TimeSpan.FromMilliseconds(100) });

		await client.Invoking(c => c.ReceiveAsync()).Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task StreamAsync_YieldsFramesThenReturnsToIdle()
	{
		using var transport = new FakeUsbTransport();
		transport.EnqueueResponse([0x00]);
		transport.EnqueueResponse([0x01, 0x02]);
		transport.EnqueueResponse([0x03, 0x04]);
		using var client = CreateClient(transport);
		var received = new List<byte[]>();

		await foreach (var signal in client.StreamAsync())
		{
			received.Add(signal.Data);
			if (received.Count == 2)
			{
				break;
			}
		}

		received.Should().HaveCount(2);
		received[0].Should().Equal(0x01, 0x02);
		received[1].Should().Equal(0x03, 0x04);
		transport.SentCommands[0].Command.Should().Be(RfCatCommands.RxMode);
		transport.SentCommands[^1].Command.Should().Be(RfCatCommands.IdleMode);
	}

	[Fact]
	public async Task StreamAsync_WhenIdleFailsOnStop_DoesNotThrow()
	{
		using var transport = new FakeUsbTransport
		{
			FailureFor = command => command == RfCatCommands.IdleMode ? new YardStickOneCommandException("idle failed") : null,
		};
		transport.EnqueueResponse([0x01, 0x02]);
		using var client = CreateClient(transport);

		await foreach (var _ in client.StreamAsync())
		{
			break;
		}

		transport.SentCommands[^1].Command.Should().Be(RfCatCommands.IdleMode);
	}

	[Fact]
	public async Task StreamAsync_StopsWhenTokenCancelled()
	{
		using var transport = new FakeUsbTransport();
		using var client = CreateClient(transport);
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

		var consume = async () =>
		{
			await foreach (var _ in client.StreamAsync(cts.Token))
			{
			}
		};

		await consume.Should().ThrowAsync<OperationCanceledException>();
		transport.SentCommands[^1].Command.Should().Be(RfCatCommands.IdleMode);
	}

	[Fact]
	public async Task Operations_AfterDispose_Throw()
	{
		using var transport = new FakeUsbTransport();
		var client = CreateClient(transport);
		client.Dispose();
		Func<Task>[] operations =
		[
			() => client.ConfigureAsync(433_920_000, Modulation.AskOok, 4800),
			async () => await client.ReceiveAsync(),
			() => client.ResetAsync(),
			async () => await client.StreamAsync().GetAsyncEnumerator().MoveNextAsync(),
		];

		foreach (var operation in operations)
		{
			await operation.Should().ThrowAsync<ObjectDisposedException>();
		}
	}

	[Fact]
	public void Dispose_CalledTwice_DisposesTransportOnce()
	{
		using var transport = new FakeUsbTransport();
		var client = CreateClient(transport);

		client.Dispose();
		client.Dispose();

		transport.DisposeCount.Should().Be(1);
	}
}
