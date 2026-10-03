using Microsoft.Extensions.Logging;
using System.Text.Json;
using YardStickOne.Api;
using YardStickOne.Api.Models;

// YardStickOne.Demo — interactive RF command recorder and player.

var logLevel = ParseLogLevel(args) ?? LogLevel.Information;
using var loggerFactory = LoggerFactory.Create(b =>
	b.AddSimpleConsole(o =>
	{
		o.TimestampFormat = "HH:mm:ss.fff ";
		o.SingleLine = true;
	})
	.SetMinimumLevel(logLevel));

var logger = loggerFactory.CreateLogger<Program>();
var commandsPath = ResolveCommandsPath();
var commands = await LoadCommandsAsync(commandsPath);

if (args.Length > 0 && args[0] is "-h" or "--help")
{
	PrintUsage(commandsPath, commands.Count == 0 ? "record" : "playback");
	return 0;
}

if (args.Length > 0 && args[0] is "devices")
{
	return ListDevices();
}

var mode = ResolveMode(args, commands);
return mode switch
{
	DemoMode.Record => await RunRecordModeAsync(args, commandsPath, commands),
	DemoMode.Playback => await RunPlaybackModeAsync(commands),
	_ => 1,
};

static int ListDevices()
{
	const string targetFragment = "vid_1d50&pid_605b";

	var allPaths = YardStickOneClient.EnumerateAllUsbDevicePaths();
	Console.WriteLine($"=== USB devices visible via SetupAPI ({allPaths.Count}) ===");

	var found = false;
	if (allPaths.Count == 0)
	{
		Console.WriteLine("  (none)");
	}
	else
	{
		foreach (var path in allPaths)
		{
			var isTarget = path.Contains(targetFragment, StringComparison.OrdinalIgnoreCase);
			if (isTarget) found = true;
			var marker = isTarget ? "  *** YARD STICK ONE ***" : string.Empty;
			Console.WriteLine($"  {path}{marker}");
		}
	}

	Console.WriteLine();
	Console.WriteLine(found
		? "RESULT: YARD Stick One IS visible via SetupAPI — WinUsbTransport should be able to open it."
		: "RESULT: YARD Stick One is NOT visible via SetupAPI. Check USB connection and driver.");

	return found ? 0 : 1;
}

static void PrintUsage(string commandsPath, string defaultMode)
{
	Console.WriteLine($"""
		YardStickOne.Demo — interactive RF command recorder/player

		Usage:
		  YardStickOne.Demo [record|playback] [--freq <Hz>] [--baud <rate>] [--modulation <mod>] [--preset <name>]

		Modulation values: ook (default), fsk, gfsk, fsk4, msk

		Presets:
		  somfy   — 433.42 MHz, FSK2, 4800 baud  (Somfy RTS roller blinds)
		  generic — 433.92 MHz, OOK,  4800 baud  (most other 433 remotes)

		Behavior:
		  - If no mode is provided and commands are empty, defaults to record mode.
		  - If no mode is provided and commands exist, defaults to playback mode.

		Commands file:
		  {commandsPath}

		Current default mode: {defaultMode}
		""");
}

DemoMode ResolveMode(string[] cliArgs, List<RecordedCommand> existingCommands)
{
	if (cliArgs.Length == 0)
	{
		return existingCommands.Count == 0 ? DemoMode.Record : DemoMode.Playback;
	}

	// Allow option-first invocation such as --log-level Debug.
	if (cliArgs[0].StartsWith("--", StringComparison.Ordinal))
	{
		return existingCommands.Count == 0 ? DemoMode.Record : DemoMode.Playback;
	}

	var modeArg = cliArgs[0].ToLowerInvariant();
	if (modeArg is "record")
	{
		return DemoMode.Record;
	}

	if (modeArg is "playback" or "replay")
	{
		return DemoMode.Playback;
	}

	Console.Error.WriteLine($"Unknown mode '{modeArg}'. Expected 'record' or 'playback'.");
	return DemoMode.Unknown;
}

async Task<int> RunRecordModeAsync(string[] cliArgs, string path, List<RecordedCommand> existingCommands)
{
	var modeArgs = SkipModeArgIfPresent(cliArgs);

	// --preset shortcuts
	var preset = ParseString(modeArgs, "--preset");
	double defaultFreq = preset?.ToLowerInvariant() switch
	{
		"somfy"   => 433_420_000,
		"generic" => 433_920_000,
		_         => 433_920_000,
	};
	Modulation defaultMod = preset?.ToLowerInvariant() switch
	{
		"somfy" => Modulation.FSK2,
		_       => Modulation.AskOok,
	};
	uint defaultBaud = preset?.ToLowerInvariant() switch
	{
		_ => 4800,
	};

	var frequencyHz = ParseDouble(modeArgs, "--freq") ?? defaultFreq;
	var baudRate    = (uint)(ParseDouble(modeArgs, "--baud") ?? defaultBaud);
	var modulation  = ParseModulation(modeArgs, "--modulation") ?? defaultMod;

	DemoLog.Recording(logger, frequencyHz / 1_000_000, modulation, baudRate, path);

	var options = new YardStickOneClientOptions
	{
		DefaultFrequencyHz = frequencyHz,
		DefaultBaudRate = baudRate,
	};

	using var client = new YardStickOneClient(options, loggerFactory.CreateLogger<YardStickOneClient>());
	await client.ConfigureAsync(frequencyHz, modulation, baudRate);

	Console.WriteLine("Record mode active. Listening for signals... (Ctrl+C to stop)");
	PrintMenu(existingCommands);

	using var cts = new CancellationTokenSource();
	Console.CancelKeyPress += (_, e) =>
	{
		e.Cancel = true;
		cts.Cancel();
	};
	var duplicateNoticeShown = false;

	while (!cts.IsCancellationRequested)
	{
		RfSignal signal;
		try
		{
			signal = await client.ReceiveAsync(cts.Token);
		}
		catch (OperationCanceledException)
		{
			if (cts.IsCancellationRequested)
				break;

			// Per-capture timeout: keep listening until user cancels.
			continue;
		}

		if (signal.Data.Length == 0)
		{
			continue;
		}

		if (ContainsSignal(existingCommands, signal))
		{
			if (!duplicateNoticeShown)
			{
				Console.WriteLine("Captured signal already exists in commands. Ignoring duplicate.");
				duplicateNoticeShown = true;
			}
			continue;
		}

		duplicateNoticeShown = false;

		Console.Write("New command captured. Enter a name (blank to skip): ");
		var name = (Console.ReadLine() ?? string.Empty).Trim();
		if (string.IsNullOrWhiteSpace(name))
		{
			Console.WriteLine("Capture skipped.");
			continue;
		}

		while (existingCommands.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
		{
			Console.Write($"Name '{name}' already exists. Enter a different name: ");
			name = (Console.ReadLine() ?? string.Empty).Trim();
			if (string.IsNullOrWhiteSpace(name))
			{
				break;
			}
		}

		if (string.IsNullOrWhiteSpace(name))
		{
			Console.WriteLine("Capture skipped.");
			continue;
		}

		existingCommands.Add(new RecordedCommand(name, CloneSignal(signal)));
		SortCommands(existingCommands);
		await SaveCommandsAsync(path, existingCommands);

		Console.WriteLine($"Saved '{name}'.");
		PrintMenu(existingCommands);
	}

	Console.WriteLine("Record mode ended.");
	return 0;
}

async Task<int> RunPlaybackModeAsync(List<RecordedCommand> existingCommands)
{
	if (existingCommands.Count == 0)
	{
		Console.WriteLine("No commands recorded yet. Start in record mode first.");
		return 1;
	}

	SortCommands(existingCommands);
	PrintMenu(existingCommands);

	using var client = new YardStickOneClient(null, loggerFactory.CreateLogger<YardStickOneClient>());

	while (true)
	{
		Console.Write("Press a number to transmit, or Q to quit: ");
		var key = Console.ReadKey(intercept: true);
		Console.WriteLine();

		if (key.KeyChar is 'q' or 'Q')
		{
			return 0;
		}

		if (!TryGetSelectedIndex(key.KeyChar, existingCommands.Count, out var selectedIndex))
		{
			Console.WriteLine("Invalid selection.");
			continue;
		}

		var selectedCommand = existingCommands[selectedIndex];
		DemoLog.Replaying(logger, selectedCommand.Name, selectedCommand.Signal.Data.Length,
			selectedCommand.Signal.FrequencyHz / 1_000_000, selectedCommand.Signal.Modulation, selectedCommand.Signal.BaudRate);

		await client.TransmitAsync(selectedCommand.Signal);
		Console.WriteLine($"Transmitted '{selectedCommand.Name}'.");
	}
}

static bool TryGetSelectedIndex(char keyChar, int commandCount, out int index)
{
	index = -1;
	if (!char.IsDigit(keyChar))
	{
		return false;
	}

	if (keyChar == '0')
	{
		if (commandCount >= 10)
		{
			index = 9;
			return true;
		}

		return false;
	}

	index = keyChar - '1';
	return index >= 0 && index < commandCount;
}

static LogLevel? ParseLogLevel(string[] args)
{
	var idx = Array.IndexOf(args, "--log-level");
	if (idx < 0 || idx + 1 >= args.Length)
		return null;
	return Enum.TryParse<LogLevel>(args[idx + 1], ignoreCase: true, out var level) ? level : null;
}

static void PrintMenu(List<RecordedCommand> commands)
{
	SortCommands(commands);
	Console.WriteLine();
	Console.WriteLine("Commands:");
	if (commands.Count == 0)
	{
		Console.WriteLine("  (none)");
		Console.WriteLine();
		return;
	}

	for (var i = 0; i < commands.Count; i++)
	{
		var prefix = i switch
		{
			< 9 => (i + 1).ToString(),
			9 => "0",
			_ => "-",
		};

		Console.WriteLine($"  {prefix}. {commands[i].Name}");
	}

	if (commands.Count > 10)
	{
		Console.WriteLine("  Note: only 1-9 and 0 (10th) are directly playable via single-key selection.");
	}

	Console.WriteLine();
}

static bool ContainsSignal(IEnumerable<RecordedCommand> commands, RfSignal signal)
	=> commands.Any(c => SignalsEqual(c.Signal, signal));

static bool SignalsEqual(RfSignal a, RfSignal b)
	=> a.FrequencyHz == b.FrequencyHz
		&& a.Modulation == b.Modulation
		&& a.BaudRate == b.BaudRate
		&& a.Data.AsSpan().SequenceEqual(b.Data);

static RfSignal CloneSignal(RfSignal signal)
	=> new()
	{
		FrequencyHz = signal.FrequencyHz,
		Modulation = signal.Modulation,
		BaudRate = signal.BaudRate,
		CapturedAt = signal.CapturedAt,
		Data = [.. signal.Data],
	};

static string ResolveCommandsPath()
{
	const string fileName = "Commands.json";
	var current = new DirectoryInfo(Environment.CurrentDirectory);

	while (current is not null)
	{
		var demoProjectFile = Path.Combine(current.FullName, "YardStickOne.Demo.csproj");
		if (File.Exists(demoProjectFile))
		{
			return Path.Combine(current.FullName, fileName);
		}

		current = current.Parent;
	}

	return Path.Combine(Environment.CurrentDirectory, fileName);
}

static async Task<List<RecordedCommand>> LoadCommandsAsync(string path)
{
	if (!File.Exists(path))
	{
		return [];
	}

	await using var stream = File.OpenRead(path);
	var commands = await JsonSerializer.DeserializeAsync<List<RecordedCommand>>(stream) ?? [];
	SortCommands(commands);
	return commands;
}

static async Task SaveCommandsAsync(string path, List<RecordedCommand> commands)
{
	SortCommands(commands);
	var options = new JsonSerializerOptions
	{
		WriteIndented = true,
	};

	await using var stream = File.Create(path);
	await JsonSerializer.SerializeAsync(stream, commands, options);
}

static void SortCommands(List<RecordedCommand> commands)
	=> commands.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

static string[] SkipModeArgIfPresent(string[] cliArgs)
{
	if (cliArgs.Length == 0)
	{
		return cliArgs;
	}

	return cliArgs[0].ToLowerInvariant() is "record" or "playback" or "replay"
		? cliArgs[1..]
		: cliArgs;
}

static double? ParseDouble(string[] args, string flag)
{
	var idx = Array.IndexOf(args, flag);
	if (idx >= 0 && idx + 1 < args.Length && double.TryParse(args[idx + 1], out var v))
	{
		return v;
	}

	return null;
}

static string? ParseString(string[] args, string flag)
{
	var idx = Array.IndexOf(args, flag);
	return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static Modulation? ParseModulation(string[] args, string flag)
{
	var val = ParseString(args, flag);
	return val?.ToLowerInvariant() switch
	{
		"ook" or "asook" or "ask" => Modulation.AskOok,
		"fsk" or "fsk2" or "2fsk" => Modulation.FSK2,
		"gfsk"                    => Modulation.GFSK,
		"fsk4" or "4fsk"          => Modulation.FSK4,
		"msk"                     => Modulation.MSK,
		null                      => null,
		var other => throw new ArgumentException($"Unknown modulation '{other}'. Use: ook, fsk, gfsk, fsk4, msk"),
	};
}

enum DemoMode
{
	Unknown,
	Record,
	Playback,
}

sealed record RecordedCommand(string Name, RfSignal Signal);

// Required for top-level logger inference
internal sealed partial class Program { }

internal static partial class DemoLog
{
	[LoggerMessage(Level = LogLevel.Information, Message = "Recording at {FreqMHz:F4} MHz, {Mod}, {Baud} baud; commands file: {File}")]
	internal static partial void Recording(ILogger logger, double freqMHz, Modulation mod, uint baud, string file);

	[LoggerMessage(Level = LogLevel.Information, Message = "Replaying '{File}' ({Bytes} bytes at {FreqMHz:F4} MHz, {Mod}, {Baud} baud)...")]
	internal static partial void Replaying(ILogger logger, string file, int bytes, double freqMHz, Modulation mod, uint baud);
}
