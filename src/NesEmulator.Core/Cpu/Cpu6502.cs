using NesEmulator.Core.Memory;

namespace NesEmulator.Core.Cpu;

/// <summary>
/// The Ricoh 2A03 core, which is a 6502 with decimal mode disabled.
///
/// The processor is driven one instruction at a time. <see cref="Step"/> fetches
/// an opcode, looks it up in <see cref="OpcodeTable"/>, works out the operand
/// address and performs the operation, charging the cycles as it goes. Cycle
/// counts matter as much as results here: the picture unit runs three of its own
/// cycles for every processor cycle, and games lean on that ratio to change
/// scroll registers partway down a frame.
/// </summary>
public sealed class Cpu6502(IBus bus)
{
    public const byte FlagCarry = 0x01;
    public const byte FlagZero = 0x02;
    public const byte FlagInterruptDisable = 0x04;
    public const byte FlagDecimal = 0x08;
    public const byte FlagBreak = 0x10;
    public const byte FlagUnused = 0x20;
    public const byte FlagOverflow = 0x40;
    public const byte FlagNegative = 0x80;

    public const ushort NmiVector = 0xFFFA;
    public const ushort ResetVector = 0xFFFC;
    public const ushort IrqVector = 0xFFFE;

    private const ushort StackBase = 0x0100;

    private readonly IBus _bus = bus;

    public byte A;
    public byte X;
    public byte Y;
    public byte S = 0xFD;
    public byte P = FlagInterruptDisable | FlagUnused;
    public ushort PC;

    /// <summary>Cycles burnt since power on. Reset seeds it with the seven the hardware spends.</summary>
    public long Cycles;

    /// <summary>Set when an undocumented opcode locks the processor up. Only reset clears it.</summary>
    public bool Jammed { get; private set; }

    private bool _nmiPending;
    private bool _irqLine;

    /// <summary>
    /// The address an indexed mode started from, before the index was added.
    /// Only the unstable stores need it, and only for the high byte.
    /// </summary>
    private ushort _baseAddress;

    public void Reset()
    {
        A = 0;
        X = 0;
        Y = 0;
        S = 0xFD;
        P = FlagInterruptDisable | FlagUnused;
        PC = Read16(ResetVector);
        Cycles = 7;
        Jammed = false;
        _nmiPending = false;
        _irqLine = false;
    }

    /// <summary>The picture unit pulls this line low at the start of vertical blank.</summary>
    public void RaiseNmi() => _nmiPending = true;

    /// <summary>The sound unit and some cartridge mappers hold this line down until serviced.</summary>
    public void SetIrqLine(bool asserted) => _irqLine = asserted;

    /// <summary>Runs one instruction, or services a pending interrupt. Returns the cycles it cost.</summary>
    public int Step()
    {
        if (Jammed)
        {
            Cycles++;
            return 1;
        }

        long start = Cycles;

        if (_nmiPending)
        {
            _nmiPending = false;
            ServiceInterrupt(NmiVector);
            return (int)(Cycles - start);
        }

        if (_irqLine && (P & FlagInterruptDisable) == 0)
        {
            ServiceInterrupt(IrqVector);
            return (int)(Cycles - start);
        }

        byte opcode = _bus.Read(PC++);
        OpcodeInfo info = OpcodeTable.Entries[opcode];
        Cycles += info.Cycles;

        ushort address = Resolve(info.Mode, info.PageCross);
        Execute(info.Op, info.Mode, address);

        return (int)(Cycles - start);
    }

    // ------------------------------------------------------------ addressing

    private ushort Resolve(Am mode, bool pageCrossPenalty)
    {
        switch (mode)
        {
            case Am.Implied:
            case Am.Accumulator:
                return 0;

            case Am.Immediate:
                return PC++;

            case Am.ZeroPage:
                return _bus.Read(PC++);

            case Am.ZeroPageX:
                return (byte)(_bus.Read(PC++) + X);

            case Am.ZeroPageY:
                return (byte)(_bus.Read(PC++) + Y);

            case Am.Relative:
            {
                sbyte offset = (sbyte)_bus.Read(PC++);
                return (ushort)(PC + offset);
            }

            case Am.Absolute:
            {
                ushort address = Read16(PC);
                PC += 2;
                return address;
            }

            case Am.AbsoluteX:
            {
                _baseAddress = Read16(PC);
                PC += 2;
                ushort address = (ushort)(_baseAddress + X);
                if (pageCrossPenalty && CrossesPage(_baseAddress, address))
                {
                    Cycles++;
                }

                return address;
            }

            case Am.AbsoluteY:
            {
                _baseAddress = Read16(PC);
                PC += 2;
                ushort address = (ushort)(_baseAddress + Y);
                if (pageCrossPenalty && CrossesPage(_baseAddress, address))
                {
                    Cycles++;
                }

                return address;
            }

            case Am.Indirect:
            {
                ushort pointer = Read16(PC);
                PC += 2;
                return Read16Wrapped(pointer);
            }

            case Am.IndexedIndirect:
            {
                byte pointer = (byte)(_bus.Read(PC++) + X);
                return Read16ZeroPage(pointer);
            }

            case Am.IndirectIndexed:
            {
                byte pointer = _bus.Read(PC++);
                _baseAddress = Read16ZeroPage(pointer);
                ushort address = (ushort)(_baseAddress + Y);
                if (pageCrossPenalty && CrossesPage(_baseAddress, address))
                {
                    Cycles++;
                }

                return address;
            }

            default:
                throw new InvalidOperationException($"Unhandled addressing mode {mode}.");
        }
    }

    private ushort Read16(ushort address) =>
        (ushort)(_bus.Read(address) | (_bus.Read((ushort)(address + 1)) << 8));

    /// <summary>Pointers in the zero page wrap inside it rather than spilling into $0100.</summary>
    private ushort Read16ZeroPage(byte address) =>
        (ushort)(_bus.Read(address) | (_bus.Read((byte)(address + 1)) << 8));

    /// <summary>
    /// The indirect jump bug. When the pointer ends at $xxFF the processor fetches
    /// the high byte from $xx00 instead of the next page, and shipped games depend
    /// on it, so the bug is part of the contract.
    /// </summary>
    private ushort Read16Wrapped(ushort address)
    {
        ushort high = (ushort)((address & 0xFF00) | (byte)(address + 1));
        return (ushort)(_bus.Read(address) | (_bus.Read(high) << 8));
    }

    private static bool CrossesPage(ushort from, ushort to) => (from & 0xFF00) != (to & 0xFF00);

    // ------------------------------------------------------------- execution

    private void Execute(Op op, Am mode, ushort address)
    {
        switch (op)
        {
            case Op.ADC: Add(_bus.Read(address)); break;
            case Op.SBC: Add((byte)(_bus.Read(address) ^ 0xFF)); break;

            case Op.AND: A &= _bus.Read(address); SetZeroNegative(A); break;
            case Op.ORA: A |= _bus.Read(address); SetZeroNegative(A); break;
            case Op.EOR: A ^= _bus.Read(address); SetZeroNegative(A); break;

            case Op.ASL:
                if (mode == Am.Accumulator)
                {
                    A = ShiftLeft(A);
                }
                else
                {
                    _bus.Write(address, ShiftLeft(ReadForModify(address)));
                }

                break;

            case Op.LSR:
                if (mode == Am.Accumulator)
                {
                    A = ShiftRight(A);
                }
                else
                {
                    _bus.Write(address, ShiftRight(ReadForModify(address)));
                }

                break;

            case Op.ROL:
                if (mode == Am.Accumulator)
                {
                    A = RotateLeft(A);
                }
                else
                {
                    _bus.Write(address, RotateLeft(ReadForModify(address)));
                }

                break;

            case Op.ROR:
                if (mode == Am.Accumulator)
                {
                    A = RotateRight(A);
                }
                else
                {
                    _bus.Write(address, RotateRight(ReadForModify(address)));
                }

                break;

            case Op.BIT:
            {
                byte value = _bus.Read(address);
                SetFlag(FlagZero, (A & value) == 0);
                SetFlag(FlagOverflow, (value & 0x40) != 0);
                SetFlag(FlagNegative, (value & 0x80) != 0);
                break;
            }

            case Op.CMP: Compare(A, _bus.Read(address)); break;
            case Op.CPX: Compare(X, _bus.Read(address)); break;
            case Op.CPY: Compare(Y, _bus.Read(address)); break;

            case Op.DEC:
            {
                byte value = (byte)(ReadForModify(address) - 1);
                _bus.Write(address, value);
                SetZeroNegative(value);
                break;
            }

            case Op.INC:
            {
                byte value = (byte)(ReadForModify(address) + 1);
                _bus.Write(address, value);
                SetZeroNegative(value);
                break;
            }

            case Op.DEX: X--; SetZeroNegative(X); break;
            case Op.DEY: Y--; SetZeroNegative(Y); break;
            case Op.INX: X++; SetZeroNegative(X); break;
            case Op.INY: Y++; SetZeroNegative(Y); break;

            case Op.LDA: A = _bus.Read(address); SetZeroNegative(A); break;
            case Op.LDX: X = _bus.Read(address); SetZeroNegative(X); break;
            case Op.LDY: Y = _bus.Read(address); SetZeroNegative(Y); break;

            case Op.STA: _bus.Write(address, A); break;
            case Op.STX: _bus.Write(address, X); break;
            case Op.STY: _bus.Write(address, Y); break;

            case Op.TAX: X = A; SetZeroNegative(X); break;
            case Op.TAY: Y = A; SetZeroNegative(Y); break;
            case Op.TXA: A = X; SetZeroNegative(A); break;
            case Op.TYA: A = Y; SetZeroNegative(A); break;
            case Op.TSX: X = S; SetZeroNegative(X); break;
            case Op.TXS: S = X; break;

            case Op.PHA: Push(A); break;
            case Op.PLA: A = Pull(); SetZeroNegative(A); break;
            case Op.PHP: Push((byte)(P | FlagBreak | FlagUnused)); break;
            case Op.PLP: P = (byte)((Pull() & ~FlagBreak) | FlagUnused); break;

            case Op.CLC: SetFlag(FlagCarry, false); break;
            case Op.SEC: SetFlag(FlagCarry, true); break;
            case Op.CLI: SetFlag(FlagInterruptDisable, false); break;
            case Op.SEI: SetFlag(FlagInterruptDisable, true); break;
            case Op.CLV: SetFlag(FlagOverflow, false); break;
            case Op.CLD: SetFlag(FlagDecimal, false); break;
            case Op.SED: SetFlag(FlagDecimal, true); break;

            case Op.BCC: Branch((P & FlagCarry) == 0, address); break;
            case Op.BCS: Branch((P & FlagCarry) != 0, address); break;
            case Op.BNE: Branch((P & FlagZero) == 0, address); break;
            case Op.BEQ: Branch((P & FlagZero) != 0, address); break;
            case Op.BPL: Branch((P & FlagNegative) == 0, address); break;
            case Op.BMI: Branch((P & FlagNegative) != 0, address); break;
            case Op.BVC: Branch((P & FlagOverflow) == 0, address); break;
            case Op.BVS: Branch((P & FlagOverflow) != 0, address); break;

            case Op.JMP: PC = address; break;

            case Op.JSR:
                // The address of the last byte of this instruction, not the next one.
                Push16((ushort)(PC - 1));
                PC = address;
                break;

            case Op.RTS: PC = (ushort)(Pull16() + 1); break;

            case Op.RTI:
                P = (byte)((Pull() & ~FlagBreak) | FlagUnused);
                PC = Pull16();
                break;

            case Op.BRK:
                PC++; // The byte after BRK is swallowed.
                Push16(PC);
                Push((byte)(P | FlagBreak | FlagUnused));
                SetFlag(FlagInterruptDisable, true);
                PC = Read16(IrqVector);
                break;

            case Op.NOP:
                // The undocumented multi-byte forms still perform the read, which is
                // why they can cost an extra cycle when indexing crosses a page.
                if (mode != Am.Implied)
                {
                    _bus.Read(address);
                }

                break;

            // ------------------------------------------------- undocumented

            case Op.SLO:
            {
                byte value = ShiftLeft(ReadForModify(address));
                _bus.Write(address, value);
                A |= value;
                SetZeroNegative(A);
                break;
            }

            case Op.RLA:
            {
                byte value = RotateLeft(ReadForModify(address));
                _bus.Write(address, value);
                A &= value;
                SetZeroNegative(A);
                break;
            }

            case Op.SRE:
            {
                byte value = ShiftRight(ReadForModify(address));
                _bus.Write(address, value);
                A ^= value;
                SetZeroNegative(A);
                break;
            }

            case Op.RRA:
            {
                byte value = RotateRight(ReadForModify(address));
                _bus.Write(address, value);
                Add(value);
                break;
            }

            case Op.SAX: _bus.Write(address, (byte)(A & X)); break;

            case Op.LAX:
                A = _bus.Read(address);
                X = A;
                SetZeroNegative(A);
                break;

            case Op.DCP:
            {
                byte value = (byte)(ReadForModify(address) - 1);
                _bus.Write(address, value);
                Compare(A, value);
                break;
            }

            case Op.ISC:
            {
                byte value = (byte)(ReadForModify(address) + 1);
                _bus.Write(address, value);
                Add((byte)(value ^ 0xFF));
                break;
            }

            case Op.ANC:
                A &= _bus.Read(address);
                SetZeroNegative(A);
                SetFlag(FlagCarry, (A & 0x80) != 0);
                break;

            case Op.ALR:
                A &= _bus.Read(address);
                A = ShiftRight(A);
                break;

            case Op.ARR:
                A &= _bus.Read(address);
                A = (byte)((A >> 1) | ((P & FlagCarry) << 7));
                SetZeroNegative(A);
                SetFlag(FlagCarry, (A & 0x40) != 0);
                SetFlag(FlagOverflow, (((A >> 6) ^ (A >> 5)) & 1) != 0);
                break;

            case Op.AXS:
            {
                byte value = _bus.Read(address);
                byte and = (byte)(A & X);
                SetFlag(FlagCarry, and >= value);
                X = (byte)(and - value);
                SetZeroNegative(X);
                break;
            }

            case Op.XAA:
                // Genuinely unstable on hardware: the result depends on analog decay
                // in the accumulator. This is the behaviour most test ROMs assume.
                A = (byte)(X & _bus.Read(address));
                SetZeroNegative(A);
                break;

            case Op.LAS:
            {
                byte value = (byte)(_bus.Read(address) & S);
                A = value;
                X = value;
                S = value;
                SetZeroNegative(value);
                break;
            }

            // The stores below drop the high byte of the target address into the
            // value being written, because the address bus is still driving it.
            case Op.AHX: _bus.Write(address, (byte)(A & X & HighByteMask())); break;
            case Op.SHY: _bus.Write(address, (byte)(Y & HighByteMask())); break;
            case Op.SHX: _bus.Write(address, (byte)(X & HighByteMask())); break;

            case Op.TAS:
                S = (byte)(A & X);
                _bus.Write(address, (byte)(S & HighByteMask()));
                break;

            case Op.JAM:
                Jammed = true;
                PC--; // Park on the offending opcode so a debugger can show it.
                break;

            default:
                throw new InvalidOperationException($"Unhandled operation {op}.");
        }
    }

    private byte HighByteMask() => (byte)((_baseAddress >> 8) + 1);

    // -------------------------------------------------------------- helpers

    /// <summary>
    /// Reads the operand of a read-modify-write instruction. The hardware writes
    /// the unchanged value back before writing the new one, and a few registers
    /// react to that first write, so it is reproduced rather than optimised away.
    /// </summary>
    private byte ReadForModify(ushort address)
    {
        byte value = _bus.Read(address);
        _bus.Write(address, value);
        return value;
    }

    private void Add(byte value)
    {
        // Decimal mode is fused off in the 2A03, so this is always binary.
        int sum = A + value + (P & FlagCarry);
        SetFlag(FlagCarry, sum > 0xFF);
        SetFlag(FlagOverflow, ((A ^ sum) & (value ^ sum) & 0x80) != 0);
        A = (byte)sum;
        SetZeroNegative(A);
    }

    private void Compare(byte register, byte value)
    {
        SetFlag(FlagCarry, register >= value);
        SetZeroNegative((byte)(register - value));
    }

    private byte ShiftLeft(byte value)
    {
        SetFlag(FlagCarry, (value & 0x80) != 0);
        byte result = (byte)(value << 1);
        SetZeroNegative(result);
        return result;
    }

    private byte ShiftRight(byte value)
    {
        SetFlag(FlagCarry, (value & 0x01) != 0);
        byte result = (byte)(value >> 1);
        SetZeroNegative(result);
        return result;
    }

    private byte RotateLeft(byte value)
    {
        int carry = P & FlagCarry;
        SetFlag(FlagCarry, (value & 0x80) != 0);
        byte result = (byte)((value << 1) | carry);
        SetZeroNegative(result);
        return result;
    }

    private byte RotateRight(byte value)
    {
        int carry = (P & FlagCarry) << 7;
        SetFlag(FlagCarry, (value & 0x01) != 0);
        byte result = (byte)((value >> 1) | carry);
        SetZeroNegative(result);
        return result;
    }

    private void Branch(bool taken, ushort target)
    {
        if (!taken)
        {
            return;
        }

        Cycles++;
        if (CrossesPage(PC, target))
        {
            Cycles++;
        }

        PC = target;
    }

    private void ServiceInterrupt(ushort vector)
    {
        Push16(PC);
        Push((byte)((P | FlagUnused) & ~FlagBreak));
        SetFlag(FlagInterruptDisable, true);
        PC = Read16(vector);
        Cycles += 7;
    }

    private void Push(byte value)
    {
        _bus.Write((ushort)(StackBase + S), value);
        S--;
    }

    private byte Pull()
    {
        S++;
        return _bus.Read((ushort)(StackBase + S));
    }

    private void Push16(ushort value)
    {
        Push((byte)(value >> 8));
        Push((byte)value);
    }

    private ushort Pull16()
    {
        byte low = Pull();
        byte high = Pull();
        return (ushort)(low | (high << 8));
    }

    private void SetFlag(byte flag, bool on)
    {
        if (on)
        {
            P |= flag;
        }
        else
        {
            P = (byte)(P & ~flag);
        }
    }

    private void SetZeroNegative(byte value)
    {
        SetFlag(FlagZero, value == 0);
        SetFlag(FlagNegative, (value & 0x80) != 0);
    }
}
