using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using YardStickOne.Api.Exceptions;

namespace YardStickOne.Api.Internal;

/// <summary>
/// WinUSB P/Invoke transport for the YARD Stick One.
/// Uses SetupAPI to enumerate devices and the native WinUSB API for bulk transfers,
/// so it works with both the Windows auto-install WinUSB path (MS_COMP_WINUSB OS descriptor)
/// and Zadig-installed devices, with no LibUsbDotNet dependency.
/// </summary>
[SuppressMessage("Major Bug", "S3453", Justification = "Instances are created only through the static Open factory in WinUsbTransport.Device.cs.")]
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

	// Win32 error code for pipe/semaphore timeout: treat as empty read rather than hard error.
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
	private const ushort RegFreq2 = 0xDF0D;
	private const ushort RegMdmcfg4 = 0xDF10;
	private const ushort RegMdmcfg3 = 0xDF11;
	private const ushort RegMdmcfg2 = 0xDF12;
	private const ushort RegPktCtrl0 = 0xDF08;   // packet control: bits[1:0] = LENGTH_CONFIG
	private const double CrystalHz = 24_000_000.0;

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
				{
					return data;
				}

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
		// Read one chunk from the IN pipe (returns empty on timeout, not a hard error).
		var chunk = ReadPipeRaw(allowTimeout: true);
		if (chunk.Length == 0)
		{
			return [];
		}

		_recvBuffer.AddRange(chunk);

		// Parse framed packets; return the first NIC_RECV payload found.
		while (TryDequeueFrame(out var app, out var cmd, out var payload))
		{
			if (app == AppNic && cmd == NicRecv)
			{
				return payload;
			}
			// Discard any other spontaneous frames (e.g. system status).
		}

		return [];
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
		{
			throw new YardStickOneCommandException(
			$"USB write incomplete: sent {transferred}/{buffer.Length} bytes.");
		}
	}

	private byte[] ReadFramedResponse(byte expectedApp, byte expectedCmd, bool allowTimeout)
	{
		var deadline = DateTime.UtcNow.AddMilliseconds(5000);

		while (true)
		{
			if (TryDequeueFrame(out var app, out var cmd, out var payload))
			{
				if (app == expectedApp && cmd == expectedCmd)
				{
					return payload;
				}

				continue;
			}

			var chunk = ReadPipeRaw(allowTimeout: true);
			if (chunk.Length == 0)
			{
				if (allowTimeout)
				{
					return [];
				}

				if (DateTime.UtcNow >= deadline)
				{
					throw new YardStickOneCommandException(
					$"Timeout waiting for response app=0x{expectedApp:X2} cmd=0x{expectedCmd:X2}.");
				}

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
			{
				_recvBuffer.Clear();
			}

			return false;
		}

		if (sof > 0)
		{
			_recvBuffer.RemoveRange(0, sof);
		}

		if (_recvBuffer.Count < 5)
		{
			return false;
		}

		var length = _recvBuffer[3] | (_recvBuffer[4] << 8);
		var total = 5 + length;
		if (_recvBuffer.Count < total)
		{
			return false;
		}

		app = _recvBuffer[1];
		cmd = _recvBuffer[2];
		payload = new byte[length];
		if (length > 0)
		{
			_recvBuffer.CopyTo(5, payload, 0, length);
		}

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
				{
					LogUsbIn(_logger, 0, string.Empty);
				}

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
		{
			return;
		}

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
