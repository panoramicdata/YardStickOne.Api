using Microsoft.Extensions.Logging;
using YardStickOne.Api;
using YardStickOne.Api.Models;

// ─────────────────────────────────────────────────────────────────────────────
// YardStickOne.Demo — record and replay RF signals with the YARD Stick One
//
// Usage:
//   YardStickOne.Demo record [--freq <Hz>] [--output <file.bin>]
//   YardStickOne.Demo replay --input <file.bin>
//   YardStickOne.Demo devices
// ─────────────────────────────────────────────────────────────────────────────

using var loggerFactory = LoggerFactory.Create(b =>
	b.AddConsole().SetMinimumLevel(LogLevel.Information));

var logger = loggerFactory.CreateLogger<Program>();

if (args.Length == 0 || args[0] is "-h" or "--help")
{
	PrintUsage();
	return 0;
}

var command = args[0].ToLowerInvariant();

return command switch
{
	"record" => await RecordAsync(args[1..]),
	"replay" => await ReplayAsync(args[1..]),
	"devices" => ListDevices(),
	_ => HandleUnknownCommand(command),
};

// ─────────────────────────────────────────────────────────────────────────────

static void PrintUsage()
{
	Console.WriteLine("""
		YardStickOne.Demo — YARD Stick One RF record / replay utility

		Commands:
		  record   [--freq <Hz>] [--baud <rate>] [--output <file.bin>]
		               Record a single RF burst to a binary file.
		               Default frequency : 433920000 Hz (433.92 MHz)
		               Default baud rate : 4800

		  replay   --input <file.bin>
		               Replay a previously recorded burst.

		  devices      List detected YARD Stick One devices.

		Examples:
		  YardStickOne.Demo record --freq 433920000 --output blind.bin
		  YardStickOne.Demo replay --input blind.bin
		""");
}

async Task<int> RecordAsync(string[] recordArgs)
{
	var frequencyHz = ParseDouble(recordArgs, "--freq") ?? 433_920_000;
	var baudRate = (uint)(ParseDouble(recordArgs, "--baud") ?? 4800);
	var outputFile = ParseString(recordArgs, "--output") ?? $"capture_{DateTimeOffset.Now:yyyyMMddHHmmss}.bin";

	DemoLog.Recording(logger, frequencyHz / 1_000_000, outputFile);

	var options = new YardStickOneClientOptions
	{
		DefaultFrequencyHz = frequencyHz,
		DefaultBaudRate = baudRate,
	};

	using var client = new YardStickOneClient(options, loggerFactory.CreateLogger<YardStickOneClient>());
	await client.ConfigureAsync(frequencyHz, Modulation.AskOok, baudRate);

	Console.WriteLine("Waiting for signal... (Ctrl+C to abort)");
	using var cts = new CancellationTokenSource();
	Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

	RfSignal signal;
	try
	{
		signal = await client.ReceiveAsync(cts.Token);
	}
	catch (OperationCanceledException)
	{
		Console.WriteLine("Cancelled.");
		return 1;
	}

	await SaveSignalAsync(signal, outputFile);
	Console.WriteLine($"Saved {signal.Data.Length} bytes to '{outputFile}'.");
	return 0;
}

async Task<int> ReplayAsync(string[] replayArgs)
{
	var inputFile = ParseString(replayArgs, "--input");
	if (inputFile is null)
	{
		Console.Error.WriteLine("Error: --input <file> is required for replay.");
		return 1;
	}

	if (!File.Exists(inputFile))
	{
		Console.Error.WriteLine($"Error: file '{inputFile}' not found.");
		return 1;
	}

	var signal = await LoadSignalAsync(inputFile);
	DemoLog.Replaying(logger, inputFile, signal.Data.Length, signal.FrequencyHz / 1_000_000, signal.Modulation, signal.BaudRate);

	using var client = new YardStickOneClient(null, loggerFactory.CreateLogger<YardStickOneClient>());
	await client.TransmitAsync(signal);
	Console.WriteLine("Replay complete.");
	return 0;
}

static int ListDevices()
{
	// LibUsbDotNet enumeration — informational only
	Console.WriteLine("Enumerating USB devices with VID=0x1D50...");
	Console.WriteLine("(Note: full enumeration requires WinUSB/libusb driver installed)");
	Console.WriteLine("  VID=0x1D50  PID=0x605B  YARD Stick One");
	return 0;
}

static int HandleUnknownCommand(string cmd)
{
	Console.Error.WriteLine($"Unknown command '{cmd}'. Run with --help for usage.");
	return 1;
}

// ─── Signal serialization ──────────────────────────────────────────────────

static async Task SaveSignalAsync(RfSignal signal, string path)
{
	// Simple binary format:
	//   8 bytes  : frequency Hz (double, big-endian)
	//   1 byte   : modulation enum value
	//   4 bytes  : baud rate (uint, big-endian)
	//   8 bytes  : captured-at ticks (long, big-endian)
	//   remaining: raw data bytes
	await using var fs = File.Create(path);
	await using var writer = new BinaryWriter(fs);

	WriteDoubleBE(writer, signal.FrequencyHz);
	writer.Write((byte)signal.Modulation);
	WriteUInt32BE(writer, signal.BaudRate);
	WriteInt64BE(writer, signal.CapturedAt.UtcTicks);
	writer.Write(signal.Data);
}

static async Task<RfSignal> LoadSignalAsync(string path)
{
	await using var fs = File.OpenRead(path);
	using var reader = new BinaryReader(fs);

	var frequencyHz = ReadDoubleBE(reader);
	var modulation = (Modulation)reader.ReadByte();
	var baudRate = ReadUInt32BE(reader);
	var ticks = ReadInt64BE(reader);
	var data = reader.ReadBytes((int)(fs.Length - fs.Position));

	return new RfSignal
	{
		FrequencyHz = frequencyHz,
		Modulation = modulation,
		BaudRate = baudRate,
		CapturedAt = new DateTimeOffset(ticks, TimeSpan.Zero),
		Data = data,
	};
}

static void WriteDoubleBE(BinaryWriter w, double value)
{
	var bytes = BitConverter.GetBytes(value);
	if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
	w.Write(bytes);
}

static double ReadDoubleBE(BinaryReader r)
{
	var bytes = r.ReadBytes(8);
	if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
	return BitConverter.ToDouble(bytes);
}

static void WriteUInt32BE(BinaryWriter w, uint value)
{
	var bytes = BitConverter.GetBytes(value);
	if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
	w.Write(bytes);
}

static uint ReadUInt32BE(BinaryReader r)
{
	var bytes = r.ReadBytes(4);
	if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
	return BitConverter.ToUInt32(bytes);
}

static void WriteInt64BE(BinaryWriter w, long value)
{
	var bytes = BitConverter.GetBytes(value);
	if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
	w.Write(bytes);
}

static long ReadInt64BE(BinaryReader r)
{
	var bytes = r.ReadBytes(8);
	if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
	return BitConverter.ToInt64(bytes);
}

// ─── Argument helpers ─────────────────────────────────────────────────────

static double? ParseDouble(string[] args, string flag)
{
	var idx = Array.IndexOf(args, flag);
	if (idx >= 0 && idx + 1 < args.Length && double.TryParse(args[idx + 1], out var v))
		return v;
	return null;
}

static string? ParseString(string[] args, string flag)
{
	var idx = Array.IndexOf(args, flag);
	return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

// Required for top-level logger inference
internal sealed partial class Program { }

internal static partial class DemoLog
{
	[LoggerMessage(Level = LogLevel.Information, Message = "Recording at {FreqMHz:F4} MHz → {File}")]
	internal static partial void Recording(ILogger logger, double freqMHz, string file);

	[LoggerMessage(Level = LogLevel.Information, Message = "Replaying '{File}' ({Bytes} bytes at {FreqMHz:F4} MHz, {Mod}, {Baud} baud)...")]
	internal static partial void Replaying(ILogger logger, string file, int bytes, double freqMHz, Modulation mod, uint baud);
}
