using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using YardStickOne.Api.Exceptions;
using YardStickOne.Api.Models;

namespace YardStickOne.Api.Internal;

/// <summary>
/// WinUSB P/Invoke transport for the YARD Stick One.
/// Uses SetupAPI to enumerate devices and the native WinUSB API for bulk transfers,
/// so it works with both the Windows auto-install WinUSB path (MS_COMP_WINUSB OS descriptor)
/// and Zadig-installed devices — no LibUsbDotNet required.
/// </summary>
internal sealed partial class WinUsbTransport : IUsbTransport
{
private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _deviceHandle;
private readonly IntPtr _winUsbHandle;
private readonly byte _endpointOut;
private readonly byte _endpointIn;
private readonly ILogger _logger;
private readonly List<byte> _recvBuffer = [];
private readonly object _sync = new();
private byte[] _pendingResponse = [];
private bool _disposed;

// Default rfcat protocol bulk endpoints used as fallback if endpoint discovery fails.
private const byte DefaultEndpointOut = 0x05;
private const byte DefaultEndpointIn = 0x85;
private const int UsbdPipeTypeBulk = 2;
private const byte StartOfFrame = (byte)'@';

// Win32 error code for pipe/semaphore timeout — treat as empty read rather than hard error.
private const int ERROR_SEM_TIMEOUT = 0x79;

// rfcat app/cmd framing constants (from rflib/const.py).
private const byte AppSystem = 0xFF;
private const byte AppNic = 0x42;
private const byte SysCmdPeek = 0x80;      // payload: bytecount_le16 + addr_le16
private const byte SysCmdPoke = 0x81;      // payload: addr_le16 + data
private const byte SysCmdPing = 0x82;      // echo payload back
private const byte SysCmdStatus = 0x83;
private const byte SysCmdPokeReg = 0x84;   // payload: addr_le16 + data (for radio SFRs)
private const byte SysCmdRfMode = 0x88;
private const byte SysCmdReset = 0x8F;
private const byte NicRecv = 0x01;
private const byte NicXmit = 0x02;

// CC1111 RF strobe values used by SYS_CMD_RFMODE.
private const byte RfstSrx = 0x02;
private const byte RfstStx = 0x03;
private const byte RfstSidle = 0x04;

// CC1111 absolute memory-mapped register addresses (radio config block base 0xDF00).
private const ushort RegFreq2   = 0xDF0D;
private const ushort RegMdmcfg4 = 0xDF10;
private const ushort RegMdmcfg3 = 0xDF11;
private const ushort RegMdmcfg2 = 0xDF12;
private const ushort RegPktCtrl0 = 0xDF08;   // packet control: bits[1:0] = LENGTH_CONFIG
private const double CrystalHz  = 24_000_000.0;

private WinUsbTransport(
Microsoft.Win32.SafeHandles.SafeFileHandle deviceHandle,
IntPtr winUsbHandle,
byte endpointOut,
byte endpointIn,
ILogger logger)
{
_deviceHandle = deviceHandle;
_winUsbHandle = winUsbHandle;
_endpointOut = endpointOut;
_endpointIn = endpointIn;
_logger = logger;
}

/// <summary>Locates and opens the YARD Stick One using SetupAPI + WinUSB.</summary>
/// <exception cref="YardStickOneNotFoundException">Device not found or could not be opened.</exception>
internal static WinUsbTransport Open(YardStickOneClientOptions options, ILogger logger)
{
var path = FindDevicePath(options.VendorId, options.ProductId)
?? throw new YardStickOneNotFoundException();

var deviceHandle = WinUsbNative.CreateFile(
path,
WinUsbNative.GENERIC_READ | WinUsbNative.GENERIC_WRITE,
WinUsbNative.FILE_SHARE_READ | WinUsbNative.FILE_SHARE_WRITE,
IntPtr.Zero,
WinUsbNative.OPEN_EXISTING,
WinUsbNative.FILE_FLAG_OVERLAPPED,
IntPtr.Zero);

if (deviceHandle.IsInvalid)
throw new YardStickOneNotFoundException();

if (!WinUsbNative.WinUsb_Initialize(deviceHandle, out var winUsbHandle))
{
deviceHandle.Dispose();
throw new YardStickOneCommandException(
$"WinUsb_Initialize failed: 0x{Marshal.GetLastWin32Error():X8}");
}

var (epOut, epIn) = DiscoverEndpoints(winUsbHandle, logger);

// Set pipe policies.
uint timeout1000 = 1000u;
uint one = 1u;
uint zero = 0u;
// OUT: 1-second transfer timeout; append ZLP if payload is multiple of maxPacket.
WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epOut, WinUsbNative.PIPE_TRANSFER_TIMEOUT, 4, ref timeout1000);
WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epOut, WinUsbNative.SHORT_PACKET_TERMINATE, 4, ref one);
WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epOut, WinUsbNative.RAW_IO, 4, ref one);
// IN: 1-second timeout; auto-clear stall; allow partial reads; raw IO.
WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epIn, WinUsbNative.PIPE_TRANSFER_TIMEOUT, 4, ref timeout1000);
WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epIn, WinUsbNative.AUTO_CLEAR_STALL, 4, ref one);
WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epIn, WinUsbNative.ALLOW_PARTIAL_READS, 4, ref one);
WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epIn, WinUsbNative.AUTO_FLUSH, 4, ref zero);
WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epIn, WinUsbNative.RAW_IO, 4, ref one);

// Flush and reset both pipes to clear stale state from previous runs.
WinUsbNative.WinUsb_AbortPipe(winUsbHandle, epIn);
WinUsbNative.WinUsb_FlushPipe(winUsbHandle, epIn);
WinUsbNative.WinUsb_ResetPipe(winUsbHandle, epOut);
WinUsbNative.WinUsb_ResetPipe(winUsbHandle, epIn);

// Best-effort startup recovery: force SIDLE then drain stale IN frames.
uint shortTimeout = 200u;
WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epIn, WinUsbNative.PIPE_TRANSFER_TIMEOUT, 4, ref shortTimeout);
TrySendIdleAndDrain(winUsbHandle, epOut, epIn);
WinUsbNative.WinUsb_SetPipePolicy(winUsbHandle, epIn, WinUsbNative.PIPE_TRANSFER_TIMEOUT, 4, ref timeout1000);

LogOpened(logger, options.VendorId, options.ProductId, path);
return new WinUsbTransport(deviceHandle, winUsbHandle, epOut, epIn, logger);
}

/// <summary>Returns the device path for the first matching VID/PID, or null if not found.</summary>
internal static string? FindDevicePath(int vid, int pid)
{
var needle = $"vid_{vid:x4}&pid_{pid:x4}";
foreach (var path in EnumerateAllDevicePaths())
{
if (path.Contains(needle, StringComparison.OrdinalIgnoreCase))
return path;
}

return null;
}

/// <summary>Returns paths for every USB device currently visible to SetupAPI.</summary>
internal static IReadOnlyList<string> EnumerateAllDevicePaths()
{
var results = new List<string>();
var guid = WinUsbNative.GUID_DEVINTERFACE_USB_DEVICE;

var devInfo = WinUsbNative.SetupDiGetClassDevs(
ref guid, null, IntPtr.Zero,
WinUsbNative.DIGCF_PRESENT | WinUsbNative.DIGCF_DEVICEINTERFACE);

if (devInfo == new IntPtr(-1))
return results;

try
{
var ifaceData = new WinUsbNative.SP_DEVICE_INTERFACE_DATA
{
cbSize = (uint)Marshal.SizeOf<WinUsbNative.SP_DEVICE_INTERFACE_DATA>(),
};

for (uint index = 0;
WinUsbNative.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref guid, index, ref ifaceData);
index++)
{
WinUsbNative.SetupDiGetDeviceInterfaceDetail(
devInfo, ref ifaceData, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);

if (requiredSize == 0)
continue;

var buffer = Marshal.AllocHGlobal((int)requiredSize);
try
{
Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);

if (!WinUsbNative.SetupDiGetDeviceInterfaceDetail(
devInfo, ref ifaceData, buffer, requiredSize, out _, IntPtr.Zero))
continue;

var path = Marshal.PtrToStringUni(buffer + 4);
if (!string.IsNullOrEmpty(path))
results.Add(path);
}
finally
{
Marshal.FreeHGlobal(buffer);
}
}
}
finally
{
WinUsbNative.SetupDiDestroyDeviceInfoList(devInfo);
}

return results;
}

/// <inheritdoc/>
public Task SendCommandAsync(byte command, byte[]? payload = null,
CancellationToken cancellationToken = default)
{
ObjectDisposedException.ThrowIf(_disposed, this);
return Task.Run(() => ExecuteCommand(command, payload), cancellationToken);
}

/// <inheritdoc/>
public Task<byte[]> ReadResponseAsync(int maxLength = 64,
CancellationToken cancellationToken = default)
{
ObjectDisposedException.ThrowIf(_disposed, this);
return Task.Run(() =>
{
lock (_sync)
{
var data = _pendingResponse;
_pendingResponse = [];
if (data.Length <= maxLength)
return data;

var truncated = new byte[maxLength];
Array.Copy(data, truncated, maxLength);
return truncated;
}
}, cancellationToken);
}

/// <inheritdoc/>
public Task<byte[]> PollIncomingFrameAsync(CancellationToken cancellationToken = default)
{
ObjectDisposedException.ThrowIf(_disposed, this);
return Task.Run(() => PollIncomingFrame(), cancellationToken);
}

private byte[] PollIncomingFrame()
{
// Read one chunk from the IN pipe (returns empty on timeout — no hard error).
var chunk = ReadPipeRaw(allowTimeout: true);
if (chunk.Length == 0)
return [];

_recvBuffer.AddRange(chunk);

// Parse framed packets; return the first NIC_RECV payload found.
while (TryDequeueFrame(out var app, out var cmd, out var payload))
{
if (app == AppNic && cmd == NicRecv)
return payload;
// Discard any other spontaneous frames (e.g. system status).
}

return [];
}

private void ExecuteCommand(byte command, byte[]? payload)
{
payload ??= [];

switch (command)
{
case RfCatCommands.Ping:
var pingPayload = payload.Length > 0 ? payload : [(byte)'P', (byte)'I', (byte)'N', (byte)'G'];
_ = TransceiveFrame(AppSystem, SysCmdPing, pingPayload, allowTimeout: false);
return;

case RfCatCommands.SetFreq:
if (payload.Length != 3)
throw new YardStickOneCommandException("SetFreq expects 3-byte payload.");
WriteRegisterBlock(RegFreq2, payload);
return;

case RfCatCommands.SetModulation:
if (payload.Length != 1)
throw new YardStickOneCommandException("SetModulation expects 1-byte payload.");
var mdmcfg2 = ReadRegister(RegMdmcfg2);
var modulation = (byte)(payload[0] & 0x70);
var newMdmcfg2 = (byte)((mdmcfg2 & 0x8F) | modulation);
WriteRegister(RegMdmcfg2, newMdmcfg2);
return;

case RfCatCommands.SetBaudRate:
if (payload.Length != 4)
throw new YardStickOneCommandException("SetBaudRate expects 4-byte payload.");
var baud = ((uint)payload[0] << 24)
| ((uint)payload[1] << 16)
| ((uint)payload[2] << 8)
| payload[3];
SetDataRate(baud);
return;

case RfCatCommands.SetSyncMode:
// payload[0] = sync_mode value (0 = no preamble/sync, see CC1111 datasheet MDMCFG2 bits[2:0]).
if (payload.Length != 1)
    throw new YardStickOneCommandException("SetSyncMode expects 1-byte payload.");
var mdmSync = ReadRegister(RegMdmcfg2);
WriteRegister(RegMdmcfg2, (byte)((mdmSync & 0xF8) | (payload[0] & 0x07)));
return;

case RfCatCommands.SetInfinitePkt:
// Set PKTCTRL0 LENGTH_CONFIG bits[1:0] = 0b10 (infinite packet length / raw stream).
var pktCtrl0 = ReadRegister(RegPktCtrl0);
WriteRegister(RegPktCtrl0, (byte)((pktCtrl0 & 0xFC) | 0x02));
return;

case RfCatCommands.RxMode:
TransceiveFrame(AppSystem, SysCmdRfMode, [RfstSrx], allowTimeout: false);
return;

case RfCatCommands.TxMode:
TransceiveFrame(AppSystem, SysCmdRfMode, [RfstStx], allowTimeout: false);
return;

case RfCatCommands.IdleMode:
TransceiveFrame(AppSystem, SysCmdRfMode, [RfstSidle], allowTimeout: false);
return;

case RfCatCommands.RecvData:
var rx = TransceiveFrame(AppNic, NicRecv, [], allowTimeout: true);
lock (_sync)
{
_pendingResponse = rx;
}
return;

case RfCatCommands.SendData:
var header = new byte[6];
var len = (ushort)Math.Min(payload.Length, ushort.MaxValue);
header[0] = (byte)(len & 0xFF);
header[1] = (byte)(len >> 8);
var xmitPayload = new byte[header.Length + payload.Length];
Array.Copy(header, xmitPayload, header.Length);
Array.Copy(payload, 0, xmitPayload, header.Length, payload.Length);
_ = TransceiveFrame(AppNic, NicXmit, xmitPayload, allowTimeout: false);
return;

case RfCatCommands.Reset:
_ = TransceiveFrame(AppSystem, SysCmdReset, [
(byte)'R', (byte)'E', (byte)'S', (byte)'E', (byte)'T', (byte)'_', (byte)'N', (byte)'O', (byte)'W', 0x00
], allowTimeout: true);
return;

default:
throw new YardStickOneCommandException($"Unsupported rfcat command byte 0x{command:X2}.");
}
}

private void SetDataRate(uint baud)
{
int drateE = -1;
int drateM = -1;
for (var e = 0; e < 16; e++)
{
var m = (int)((baud * Math.Pow(2, 28)) / (Math.Pow(2, e) * CrystalHz) - 256 + 0.5);
if (m is >= 0 and < 256)
{
drateE = e;
drateM = m;
break;
}
}

if (drateE < 0)
throw new YardStickOneCommandException($"Baud rate {baud} cannot be represented.");

var mdmcfg4 = ReadRegister(RegMdmcfg4);
var newMdmcfg4 = (byte)((mdmcfg4 & 0xF0) | (drateE & 0x0F));
WriteRegister(RegMdmcfg3, (byte)drateM);
WriteRegister(RegMdmcfg4, newMdmcfg4);
}

private byte ReadRegister(ushort address)
{
var payload = new byte[4];
payload[0] = 0x01;
payload[1] = 0x00;
payload[2] = (byte)(address & 0xFF);
payload[3] = (byte)(address >> 8);
var response = TransceiveFrame(AppSystem, SysCmdPeek, payload, allowTimeout: false);
return response.Length > 0 ? response[0] : (byte)0;
}

private void WriteRegister(ushort address, byte value)
=> WriteRegisterBlock(address, [value]);

private void WriteRegisterBlock(ushort address, byte[] values)
{
var payload = new byte[2 + values.Length];
payload[0] = (byte)(address & 0xFF);
payload[1] = (byte)(address >> 8);
Array.Copy(values, 0, payload, 2, values.Length);
_ = TransceiveFrame(AppSystem, SysCmdPoke, payload, allowTimeout: false);
}

private byte[] TransceiveFrame(byte app, byte cmd, byte[] payload, bool allowTimeout)
{
var frame = new byte[4 + payload.Length];
frame[0] = app;
frame[1] = cmd;
frame[2] = (byte)(payload.Length & 0xFF);
frame[3] = (byte)(payload.Length >> 8);
Array.Copy(payload, 0, frame, 4, payload.Length);

WritePipeRaw(frame);
return ReadFramedResponse(app, cmd, allowTimeout);
}

private void WritePipeRaw(byte[] buffer)
{
if (_logger.IsEnabled(LogLevel.Debug))
{
var hex = BitConverter.ToString(buffer);
LogUsbOut(_logger, buffer[1], buffer.Length, hex);
}

if (!WinUsbNative.WinUsb_WritePipe(_winUsbHandle, _endpointOut,
buffer, (uint)buffer.Length, out var transferred, IntPtr.Zero))
{
var err = Marshal.GetLastWin32Error();
throw new YardStickOneCommandException(
$"USB write failed: Win32 0x{err:X8} (transferred {transferred}/{buffer.Length})");
}

if (transferred != (uint)buffer.Length)
throw new YardStickOneCommandException(
$"USB write incomplete: sent {transferred}/{buffer.Length} bytes.");
}

private byte[] ReadFramedResponse(byte expectedApp, byte expectedCmd, bool allowTimeout)
{
var deadline = DateTime.UtcNow.AddMilliseconds(5000);

while (true)
{
if (TryDequeueFrame(out var app, out var cmd, out var payload))
{
if (app == expectedApp && cmd == expectedCmd)
return payload;

continue;
}

var chunk = ReadPipeRaw(allowTimeout: true);
if (chunk.Length == 0)
{
if (allowTimeout)
return [];

if (DateTime.UtcNow >= deadline)
throw new YardStickOneCommandException(
$"Timeout waiting for response app=0x{expectedApp:X2} cmd=0x{expectedCmd:X2}.");

continue;
}

_recvBuffer.AddRange(chunk);
}
}

private bool TryDequeueFrame(out byte app, out byte cmd, out byte[] payload)
{
app = 0;
cmd = 0;
payload = [];

var sof = _recvBuffer.IndexOf(StartOfFrame);
if (sof < 0)
{
if (_recvBuffer.Count > 1024)
_recvBuffer.Clear();
return false;
}

if (sof > 0)
_recvBuffer.RemoveRange(0, sof);

if (_recvBuffer.Count < 5)
return false;

var length = _recvBuffer[3] | (_recvBuffer[4] << 8);
var total = 5 + length;
if (_recvBuffer.Count < total)
return false;

app = _recvBuffer[1];
cmd = _recvBuffer[2];
payload = new byte[length];
if (length > 0)
_recvBuffer.CopyTo(5, payload, 0, length);

_recvBuffer.RemoveRange(0, total);
return true;
}

private byte[] ReadPipeRaw(bool allowTimeout)
{
var buffer = new byte[512];
if (!WinUsbNative.WinUsb_ReadPipe(_winUsbHandle, _endpointIn,
buffer, (uint)buffer.Length, out var transferred, IntPtr.Zero))
{
var err = Marshal.GetLastWin32Error();
if (err == ERROR_SEM_TIMEOUT && allowTimeout)
{
if (_logger.IsEnabled(LogLevel.Debug))
LogUsbIn(_logger, 0, string.Empty);
return [];
}

throw new YardStickOneCommandException($"USB read failed: Win32 0x{err:X8}");
}

var result = new byte[transferred];
Array.Copy(buffer, result, (int)transferred);
if (_logger.IsEnabled(LogLevel.Debug))
{
var hex = BitConverter.ToString(result);
LogUsbIn(_logger, (int)transferred, hex);
}

return result;
}

/// <inheritdoc/>
public void Dispose()
{
if (_disposed)
return;
_disposed = true;
TrySendSidle();
WinUsbNative.WinUsb_Free(_winUsbHandle);
_deviceHandle.Dispose();
}

private void TrySendSidle()
{
try
{
byte[] frame = [AppSystem, SysCmdRfMode, 1, 0, RfstSidle];
WinUsbNative.WinUsb_WritePipe(_winUsbHandle, _endpointOut, frame, (uint)frame.Length, out _, IntPtr.Zero);
var buf = new byte[64];
WinUsbNative.WinUsb_ReadPipe(_winUsbHandle, _endpointIn, buf, (uint)buf.Length, out _, IntPtr.Zero);
}
catch
{
// Best effort only; close should continue even if SIDLE cannot be sent.
}
}

private static void TrySendIdleAndDrain(IntPtr winUsbHandle, byte epOut, byte epIn)
{
try
{
// Send SIDLE up to 5 times with a short pause between attempts;
// the OUT pipe may NAK initially if the device is mid-RX interrupt.
byte[] frame = [AppSystem, SysCmdRfMode, 1, 0, RfstSidle];
for (var attempt = 0; attempt < 5; attempt++)
{
if (WinUsbNative.WinUsb_WritePipe(winUsbHandle, epOut, frame, (uint)frame.Length, out _, IntPtr.Zero))
break;
Thread.Sleep(100);
}

// Drain the IN pipe until it is quiet: keep reading until we get two
// consecutive empty reads (each times out after 200 ms), which means
// the device has stopped streaming stale RF frames.
var buf = new byte[512];
int emptyStreak = 0;
while (emptyStreak < 2)
{
if (!WinUsbNative.WinUsb_ReadPipe(winUsbHandle, epIn, buf, (uint)buf.Length, out var n, IntPtr.Zero) || n == 0)
emptyStreak++;
else
emptyStreak = 0;
}
}
catch
{
// Best effort only.
}
}

private static (byte epOut, byte epIn) DiscoverEndpoints(IntPtr winUsbHandle, ILogger logger)
{
if (!WinUsbNative.WinUsb_QueryInterfaceSettings(winUsbHandle, 0, out var ifaceDesc))
{
LogEndpoints(logger, DefaultEndpointOut, DefaultEndpointIn);
return (DefaultEndpointOut, DefaultEndpointIn);
}

byte epOut = DefaultEndpointOut, epIn = DefaultEndpointIn;
for (byte i = 0; i < ifaceDesc.bNumEndpoints; i++)
{
if (!WinUsbNative.WinUsb_QueryPipe(winUsbHandle, 0, i, out var pipe))
continue;

if (logger.IsEnabled(LogLevel.Debug))
{
var pipeType = PipeTypeName(pipe.PipeType);
LogPipe(logger, i, pipe.PipeId, pipeType, pipe.MaximumPacketSize);
}

if (pipe.PipeType != UsbdPipeTypeBulk)
continue;

if ((pipe.PipeId & 0x80) != 0)
epIn = pipe.PipeId;
else
epOut = pipe.PipeId;
}

LogEndpoints(logger, epOut, epIn);
return (epOut, epIn);
}

private static string PipeTypeName(int pipeType)
=> pipeType switch
{
0 => "Control",
1 => "Isochronous",
2 => "Bulk",
3 => "Interrupt",
_ => $"Unknown({pipeType})",
};

[LoggerMessage(Level = LogLevel.Information,
Message = "Opened YARD Stick One (VID=0x{Vid:X4} PID=0x{Pid:X4}) at {Path}")]
private static partial void LogOpened(ILogger logger, int vid, int pid, string path);

[LoggerMessage(Level = LogLevel.Debug, Message = "USB OUT cmd=0x{Cmd:X2} len={Len} bytes={Hex}")]
private static partial void LogUsbOut(ILogger logger, byte cmd, int len, string hex);

[LoggerMessage(Level = LogLevel.Debug, Message = "USB IN  len={Len} bytes={Hex}")]
private static partial void LogUsbIn(ILogger logger, int len, string hex);

[LoggerMessage(Level = LogLevel.Debug, Message = "USB pipe idx={Index} id=0x{PipeId:X2} type={Type} maxPacket={MaxPacket}")]
private static partial void LogPipe(ILogger logger, int index, byte pipeId, string type, int maxPacket);

[LoggerMessage(Level = LogLevel.Information, Message = "Using USB endpoints OUT=0x{EpOut:X2} IN=0x{EpIn:X2}")]
private static partial void LogEndpoints(ILogger logger, byte epOut, byte epIn);
}
