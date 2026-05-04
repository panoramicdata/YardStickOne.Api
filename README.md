# YardStickOne.Api

A .NET library for controlling the [Great Scott Gadgets YARD Stick One](https://greatscottgadgets.com/YardStickOne/) software-defined radio via USB.

[![Codacy Badge](https://app.codacy.com/project/badge/Grade/e3cdd93e8658432e81b6b343ff8c6c39)](https://app.codacy.com/gh/panoramicdata/YardStickOne.Api/dashboard?utm_source=gh&utm_medium=referral&utm_content=&utm_campaign=Badge_grade)
[![NuGet](https://img.shields.io/nuget/v/YardStickOne.Api)](https://www.nuget.org/packages/YardStickOne.Api)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## Supported devices

| Device | VID | PID | Status |
|--------|-----|-----|--------|
| YARD Stick One | 0x1D50 | 0x605B | ✓ |

## Features

- Configure frequency, modulation and baud rate
- Receive (record) RF signal bursts
- Transmit (replay) recorded RF signals
- Supports ASK/OOK, FSK2, GFSK, FSK4, MSK modulations
- Built-in support for 433.92 MHz OOK devices (e.g. Somfy RTS blinds remotes)
- Fully async API with cancellation support
- Testable via `IYardStickOneClient` interface

## Requirements

- .NET 10+
- YARD Stick One with [WinUSB driver installed](https://greatscottgadgets.com/YardStickOne/) (Windows) or appropriate udev rules (Linux)

## Installation

```
dotnet add package YardStickOne.Api
```

## Quick start

```csharp
using YardStickOne.Api;
using YardStickOne.Api.Models;

// Open device with defaults (433.92 MHz, ASK/OOK, 4800 baud)
using var client = new YardStickOneClient();

// Record a signal
var signal = await client.ReceiveAsync();
Console.WriteLine($"Captured {signal.Data.Length} bytes");

// Replay it
await client.TransmitAsync(signal);
```

## Demo

```
dotnet run --project YardStickOne.Demo -- record --freq 433920000 --output blind.bin
dotnet run --project YardStickOne.Demo -- replay --input blind.bin
```

## License

MIT — see [LICENSE](LICENSE).
