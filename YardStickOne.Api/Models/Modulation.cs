namespace YardStickOne.Api.Models;

/// <summary>
/// RF modulation scheme.
/// </summary>
public enum Modulation : byte
{
	/// <summary>2-FSK (two-level frequency-shift keying).</summary>
	FSK2 = 0x00,

	/// <summary>GFSK (Gaussian-filtered FSK).</summary>
	GFSK = 0x10,

	/// <summary>ASK / OOK (amplitude-shift keying / on-off keying).</summary>
	AskOok = 0x30,

	/// <summary>4-FSK (four-level frequency-shift keying).</summary>
	FSK4 = 0x40,

	/// <summary>MSK (minimum-shift keying).</summary>
	MSK = 0x70,
}
