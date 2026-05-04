namespace YardStickOne.Api.Tests;

public sealed class RfSignalTests
{
	[Fact]
	public void Constructor_SetsProperties()
	{
		var data = new byte[] { 0x01, 0x02 };
		var captured = DateTimeOffset.UtcNow;

		var signal = new RfSignal
		{
			FrequencyHz = 433_920_000,
			Modulation = Modulation.AskOok,
			BaudRate = 4800,
			Data = data,
			CapturedAt = captured,
		};

		signal.FrequencyHz.Should().Be(433_920_000);
		signal.Modulation.Should().Be(Modulation.AskOok);
		signal.BaudRate.Should().Be(4800u);
		signal.Data.Should().BeSameAs(data);
		signal.CapturedAt.Should().Be(captured);
	}
}
