using YardStickOne.Api.Exceptions;

namespace YardStickOne.Api.Internal;

/// <summary>
/// Translates rfcat command bytes into CC1111 register and radio-mode frames.
/// </summary>
internal sealed partial class WinUsbTransport
{
	private void ExecuteCommand(byte command, byte[]? payload)
	{
		payload ??= [];

		if (TryExecuteModeCommand(command))
		{
			return;
		}

		if (TryExecuteConfigCommand(command, payload))
		{
			return;
		}

		ExecuteDataCommand(command, payload);
	}

	private bool TryExecuteModeCommand(byte command)
	{
		switch (command)
		{
			case RfCatCommands.RxMode:
				TransceiveFrame(AppSystem, SysCmdRfMode, [RfstSrx], allowTimeout: false);
				return true;

			case RfCatCommands.TxMode:
				TransceiveFrame(AppSystem, SysCmdRfMode, [RfstStx], allowTimeout: false);
				return true;

			case RfCatCommands.IdleMode:
				TransceiveFrame(AppSystem, SysCmdRfMode, [RfstSidle], allowTimeout: false);
				return true;

			case RfCatCommands.Reset:
				_ = TransceiveFrame(AppSystem, SysCmdReset, [
				(byte)'R', (byte)'E', (byte)'S', (byte)'E', (byte)'T', (byte)'_', (byte)'N', (byte)'O', (byte)'W', 0x00
				], allowTimeout: true);
				return true;

			default:
				return false;
		}
	}

	private bool TryExecuteConfigCommand(byte command, byte[] payload)
	{
		switch (command)
		{
			case RfCatCommands.SetFreq:
				SetFrequency(payload);
				return true;

			case RfCatCommands.SetModulation:
				SetModulation(payload);
				return true;

			case RfCatCommands.SetBaudRate:
				SetBaudRate(payload);
				return true;

			case RfCatCommands.SetSyncMode:
				SetSyncMode(payload);
				return true;

			case RfCatCommands.SetInfinitePkt:
				SetInfinitePacketLength();
				return true;

			default:
				return false;
		}
	}

	private void ExecuteDataCommand(byte command, byte[] payload)
	{
		switch (command)
		{
			case RfCatCommands.Ping:
				var pingPayload = payload.Length > 0 ? payload : [(byte)'P', (byte)'I', (byte)'N', (byte)'G'];
				_ = TransceiveFrame(AppSystem, SysCmdPing, pingPayload, allowTimeout: false);
				return;

			case RfCatCommands.RecvData:
				ReceiveData();
				return;

			case RfCatCommands.SendData:
				SendData(payload);
				return;

			default:
				throw new YardStickOneCommandException($"Unsupported rfcat command byte 0x{command:X2}.");
		}
	}

	private void SetFrequency(byte[] payload)
	{
		if (payload.Length != 3)
		{
			throw new YardStickOneCommandException("SetFreq expects 3-byte payload.");
		}

		WriteRegisterBlock(RegFreq2, payload);
	}

	private void SetModulation(byte[] payload)
	{
		if (payload.Length != 1)
		{
			throw new YardStickOneCommandException("SetModulation expects 1-byte payload.");
		}

		var mdmcfg2 = ReadRegister(RegMdmcfg2);
		var modulation = (byte)(payload[0] & 0x70);
		var newMdmcfg2 = (byte)((mdmcfg2 & 0x8F) | modulation);
		WriteRegister(RegMdmcfg2, newMdmcfg2);
	}

	private void SetBaudRate(byte[] payload)
	{
		if (payload.Length != 4)
		{
			throw new YardStickOneCommandException("SetBaudRate expects 4-byte payload.");
		}

		var baud = ((uint)payload[0] << 24)
		| ((uint)payload[1] << 16)
		| ((uint)payload[2] << 8)
		| payload[3];
		SetDataRate(baud);
	}

	private void SetSyncMode(byte[] payload)
	{
		// payload[0] = sync_mode value (0 = no preamble/sync, see CC1111 datasheet MDMCFG2 bits[2:0]).
		if (payload.Length != 1)
		{
			throw new YardStickOneCommandException("SetSyncMode expects 1-byte payload.");
		}

		var mdmSync = ReadRegister(RegMdmcfg2);
		WriteRegister(RegMdmcfg2, (byte)((mdmSync & 0xF8) | (payload[0] & 0x07)));
	}

	private void SetInfinitePacketLength()
	{
		// Set PKTCTRL0 LENGTH_CONFIG bits[1:0] = 0b10 (infinite packet length / raw stream).
		var pktCtrl0 = ReadRegister(RegPktCtrl0);
		WriteRegister(RegPktCtrl0, (byte)((pktCtrl0 & 0xFC) | 0x02));
	}

	private void ReceiveData()
	{
		var rx = TransceiveFrame(AppNic, NicRecv, [], allowTimeout: true);
		lock (_sync)
		{
			_pendingResponse = rx;
		}
	}

	private void SendData(byte[] payload)
	{
		var header = new byte[6];
		var len = (ushort)Math.Min(payload.Length, ushort.MaxValue);
		header[0] = (byte)(len & 0xFF);
		header[1] = (byte)(len >> 8);
		var xmitPayload = new byte[header.Length + payload.Length];
		Array.Copy(header, xmitPayload, header.Length);
		Array.Copy(payload, 0, xmitPayload, header.Length, payload.Length);
		_ = TransceiveFrame(AppNic, NicXmit, xmitPayload, allowTimeout: false);
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
		{
			throw new YardStickOneCommandException($"Baud rate {baud} cannot be represented.");
		}

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
}
