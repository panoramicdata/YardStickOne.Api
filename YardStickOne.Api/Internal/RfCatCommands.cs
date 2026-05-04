/// <summary>
/// rfcat USB command bytes for the YARD Stick One CC1111 firmware.
/// See https://github.com/atlas0fd00m/rfcat for the canonical list.
/// </summary>
internal static class RfCatCommands
{
	// --- Radio configuration ---
	internal const byte SetFreq = 0x30;
	internal const byte SetModulation = 0x31;
	internal const byte SetBaudRate = 0x32;
	internal const byte SetSyncWord = 0x33;
	internal const byte SetSyncMode = 0x34;
	internal const byte SetMaxPower = 0x35;
	internal const byte SetRxFilter = 0x36;

	// --- RX / TX ---
	internal const byte RxMode = 0x40;
	internal const byte TxMode = 0x41;
	internal const byte IdleMode = 0x42;
	internal const byte RecvData = 0x43;
	internal const byte SendData = 0x44;

	// --- Device control ---
	internal const byte Ping = 0x71;
	internal const byte Reset = 0x72;
	internal const byte GetSerial = 0x73;
}
