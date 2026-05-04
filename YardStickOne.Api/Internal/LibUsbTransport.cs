using LibUsbDotNet;
using LibUsbDotNet.Main;
using Microsoft.Extensions.Logging;
using YardStickOne.Api.Exceptions;
using YardStickOne.Api.Models;

namespace YardStickOne.Api.Internal;

/// <summary>
/// LibUsbDotNet-backed USB bulk-transfer transport for the YARD Stick One.
/// </summary>
internal sealed partial class LibUsbTransport : IUsbTransport
{
	private readonly UsbDevice _device;
	private readonly UsbEndpointWriter _writer;
	private readonly UsbEndpointReader _reader;
	private readonly ILogger _logger;
	private bool _disposed;

	// rfcat protocol endpoints
	private const byte EndpointOut = 0x05;
	private const byte EndpointIn = 0x85;

	internal LibUsbTransport(UsbDevice device, ILogger logger)
	{
		_device = device;
		_logger = logger;
		_writer = device.OpenEndpointWriter(WriteEndpointID.Ep05);
		_reader = device.OpenEndpointReader(ReadEndpointID.Ep05);
	}

	/// <summary>
	/// Locates and opens the YARD Stick One, returning a new <see cref="LibUsbTransport"/>.
	/// </summary>
	internal static LibUsbTransport Open(YardStickOneClientOptions options, ILogger logger)
	{
		var finder = new UsbDeviceFinder(options.VendorId, options.ProductId);
		var device = UsbDevice.OpenUsbDevice(finder)
			?? throw new YardStickOneNotFoundException();

		LogOpened(logger, (int)options.VendorId, (int)options.ProductId);
		return new LibUsbTransport(device, logger);
	}

	/// <inheritdoc/>
	public Task SendCommandAsync(byte command, byte[]? payload = null, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		var buffer = payload is { Length: > 0 }
			? new byte[] { command }.Concat(payload).ToArray()
			: [command];

		LogUsbOut(_logger, command, buffer.Length);
		return Task.Run(() =>
		{
			var ec = _writer.Write(buffer, 1000, out var transferred);
			if (ec != ErrorCode.None)
				throw new YardStickOneCommandException($"USB write failed: {ec} (transferred {transferred}/{buffer.Length})");
		}, cancellationToken);
	}

	/// <inheritdoc/>
	public Task<byte[]> ReadResponseAsync(int maxLength = 64, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return Task.Run(() =>
		{
			var buffer = new byte[maxLength];
			var ec = _reader.Read(buffer, 2000, out var transferred);
			if (ec != ErrorCode.None && ec != ErrorCode.Win32Error)
				throw new YardStickOneCommandException($"USB read failed: {ec}");
			LogUsbIn(_logger, transferred);
			var result = new byte[transferred];
			Array.Copy(buffer, result, transferred);
			return result;
		}, cancellationToken);
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_writer.Dispose();
		_reader.Dispose();
		_device.Close();
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Opened YARD Stick One (VID=0x{Vid:X4} PID=0x{Pid:X4})")]
	private static partial void LogOpened(ILogger logger, int vid, int pid);

	[LoggerMessage(Level = LogLevel.Debug, Message = "USB OUT cmd=0x{Cmd:X2} len={Len}")]
	private static partial void LogUsbOut(ILogger logger, byte cmd, int len);

	[LoggerMessage(Level = LogLevel.Debug, Message = "USB IN len={Len}")]
	private static partial void LogUsbIn(ILogger logger, int len);
}
