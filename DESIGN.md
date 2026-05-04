# DESIGN.md — YardStickOne.Api

## Overview

`YardStickOne.Api` wraps the Great Scott Gadgets YARD Stick One USB transceiver in a clean .NET abstraction. The design follows the same pattern used in `Maschine.Api` and other PanoramicData USB device libraries: a public client interface backed by an internal transport abstraction, allowing unit tests to run without hardware.

---

## Device

| Property | Value |
|----------|-------|
| Manufacturer | Great Scott Gadgets |
| Device | YARD Stick One |
| USB VID | `0x1D50` |
| USB PID | `0x605B` |
| RF chip | Texas Instruments CC1111 |
| Default frequency | 433.92 MHz (ISM band) |
| Default modulation | ASK/OOK |
| Default baud rate | 4800 bps |
| USB bulk endpoint | EP5 (in + out) |

The device uses the `rfcat` protocol — short ASCII/binary command frames sent over USB bulk transfer.

---

## Architecture

```
┌────────────────────────────────┐
│  Consumer code                 │
│  (Program.cs / unit tests)     │
└────────────────┬───────────────┘
                 │ IYardStickOneClient
┌────────────────▼───────────────┐
│  YardStickOneClient            │  public sealed partial class
│  - ConfigureAsync              │
│  - ReceiveAsync                │
│  - TransmitAsync(RfSignal)     │
│  - TransmitAsync(byte[])       │
│  - ResetAsync                  │
└────────────────┬───────────────┘
                 │ IUsbTransport (internal)
        ┌────────┴────────┐
        │                 │
┌───────▼──────┐  ┌───────▼──────────┐
│LibUsbTransport│  │ FakeUsbTransport  │
│(LibUsbDotNet) │  │ (tests/demo)      │
└───────────────┘  └──────────────────┘
```

---

## Public surface

### `IYardStickOneClient`

```csharp
Task ConfigureAsync(double frequencyHz, Modulation modulation, uint baudRate, CancellationToken ct = default);
Task<RfSignal> ReceiveAsync(CancellationToken ct = default);
Task TransmitAsync(RfSignal signal, CancellationToken ct = default);
Task TransmitAsync(byte[] data, CancellationToken ct = default);
Task ResetAsync(CancellationToken ct = default);
```

### `RfSignal` (record)

```csharp
double FrequencyHz
Modulation Modulation
uint BaudRate
byte[] Data
DateTimeOffset CapturedAt
```

### `Modulation` (enum)

| Member | CC1111 value | Description |
|--------|-------------|-------------|
| `AskOok` | 3 | Amplitude-shift keying / OOK |
| `Fsk2` | 0 | 2-FSK |
| `Gfsk` | 1 | GFSK |
| `Fsk4` | 4 | 4-FSK |
| `Msk` | 7 | MSK |

### `YardStickOneClientOptions`

```csharp
int VendorId           = 0x1D50
int ProductId          = 0x605B
double DefaultFrequencyHz  = 433_920_000
Modulation DefaultModulation = AskOok
uint DefaultBaudRate   = 4800
TimeSpan ReceiveTimeout    = 10 s
```

---

## rfcat protocol summary

Commands are short ASCII frames terminated by `\n` and sent over USB bulk EP5 OUT. Responses arrive on EP5 IN.

| Command | Purpose |
|---------|---------|
| `s FREQ <word>\n` | Set frequency (24-bit frequency word) |
| `s MDMCFG4 <val>\n` | Set modulation / channel BW |
| `s DRATE_M <val>\n` | Set baud rate mantissa |
| `s DRATE_E <val>\n` | Set baud rate exponent |
| `RX\n` | Start receive mode |
| `b <len> <data>\n` | Transmit raw burst |
| `RESET\n` | Reset CC1111 |

### Frequency word calculation

$$
\text{freqWord} = \frac{f_{\text{Hz}}}{\frac{26\,\text{MHz}}{2^{16}}} = \frac{f_{\text{Hz}} \times 65536}{26\,000\,000}
$$

Example: 433.92 MHz → freqWord = `0x10A762`

---

## Internal transport

`IUsbTransport` exposes two methods:

```csharp
Task SendCommandAsync(string command, CancellationToken ct = default);
Task<byte[]> ReadResponseAsync(int length, CancellationToken ct = default);
```

`LibUsbTransport` opens the device using `UsbDeviceFinder(vendorId, productId)`, claims the interface, and uses `UsbEndpointWriter`/`UsbEndpointReader` on `WriteEndpointID.Ep05` / `ReadEndpointID.Ep05`.

---

## Testability

`FakeUsbTransport` is included in the test project. It queues outbound commands in a `ConcurrentQueue<string>` and lets tests inject response bytes. All 18 unit tests run without hardware using this fake.

---

## Versioning

`version.json` sets `"version": "0.1"` with Nerdbank.GitVersioning. The NuGet package version is derived from the git commit graph. During development (before the first tagged release) packages carry a `-g` prerelease suffix (e.g. `0.1.0-g`).

---

## Project structure

```
YardStickOne.Api/
├── YardStickOne.Api/          # Library (NuGet package)
│   ├── IYardStickOneClient.cs
│   ├── YardStickOneClient.cs
│   ├── Models/
│   │   ├── Modulation.cs
│   │   ├── RfSignal.cs
│   │   └── YardStickOneClientOptions.cs
│   └── Internal/
│       ├── IUsbTransport.cs
│       └── LibUsbTransport.cs
├── YardStickOne.Demo/         # Console demo (record / replay)
│   └── Program.cs
└── YardStickOne.Api.Tests/    # xUnit v3 unit tests
    ├── FakeUsbTransport.cs
    ├── GlobalUsings.cs
    └── YardStickOneClientTests.cs
```
