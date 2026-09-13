namespace NesEmulator.Core.Cpu;

/// <summary>Deterministic models of the unstable immediate LAX ($AB) opcode.</summary>
public enum AbOpcodeProfile : byte
{
    MaskEE = 0xEE,
    MaskFF = 0xFF,
}
