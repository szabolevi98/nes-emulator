using NesEmulator.Core.Memory;

namespace NesEmulator.Core.Cpu;

/// <summary>
/// The Ricoh 2A03 core, which is a 6502 with decimal mode disabled.
///
/// The processor is driven one instruction at a time. <see cref="Step"/> fetches
/// an opcode, looks it up in <see cref="OpcodeTable"/>, works out the operand
/// address and performs the operation. Every cycle is a bus read or write,
/// including dummy reads; the opcode table never pads elapsed time. Cycle
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
    public byte S;
    public byte P = FlagInterruptDisable | FlagUnused;
    public ushort PC;

    /// <summary>Cycles since power on. Each reset adds seven real bus cycles.</summary>
    public long Cycles;

    /// <summary>Set when an undocumented opcode locks the processor up. Only reset clears it.</summary>
    public bool Jammed { get; private set; }

    private bool _nmiPending;
    private bool _irqLine;
    private bool _irqReady;
    private bool _irqSample;
    private bool _previousIrqSample;
    private bool _nmiReady;
    private bool _nmiSample;
    private bool _previousNmiSample;
    private bool _lastNmiLine;
    private bool _stepping;
    private byte _jamPhase;

    /// <summary>
    /// The address an indexed mode started from, before the index was added.
    /// Only the unstable stores need it, and only for the high byte.
    /// </summary>
    private ushort _baseAddress;

    public void Reset()
    {
        Jammed = false;
        _jamPhase = 0;
        _nmiPending = false;
        _lastNmiLine = false;
        _irqLine = false;
        Read(PC);
        Read(PC);
        for (int i = 0; i < 3; i++)
        {
            Read((ushort)(StackBase + S));
            S--;
        }
        P = (byte)((P | FlagInterruptDisable | FlagUnused) & ~FlagBreak);
        PC = Read16(ResetVector);
        _irqReady = _irqSample = _previousIrqSample = false;
        _nmiReady = _nmiSample = _previousNmiSample = false;
    }

    /// <summary>The picture unit pulls this line low at the start of vertical blank.</summary>
    public void RaiseNmi()
    {
        _nmiPending = true;
        if (!_stepping) _nmiReady = true;
    }

    /// <summary>The sound unit and some cartridge mappers hold this line down until serviced.</summary>
    public void SetIrqLine(bool asserted) => _irqLine = asserted;

    /// <summary>
    /// Called once for every cycle the processor spends, as it spends it. The
    /// console uses this to run the picture and sound units alongside the
    /// instruction rather than after it, which is what lets a game read a picture
    /// register partway through and see the value the hardware would have shown.
    /// </summary>
    public Action? OnCycle { get; set; }

    /// <summary>Completes the bus cycle before interrupt input sampling.</summary>
    public Action? OnCycleComplete { get; set; }

    /// <summary>Logical assertion of the NMI input, sampled after the bus access.</summary>
    public Func<bool>? NmiInput { get; set; }

    /// <summary>Runs one instruction, or services a pending interrupt. Returns the cycles it cost.</summary>
    public int Step()
    {
        if (Jammed)
        {
            Read(_jamPhase is 1 or 2 ? (ushort)0xFFFE : (ushort)0xFFFF);
            if (_jamPhase < 3) _jamPhase++;
            return 1;
        }

        long start = Cycles;
        _stepping = true;
        bool pollInterrupts = false;
        bool branchPoll = false;
        bool branchIrq = false;
        bool branchNmi = false;

        if (_nmiReady)
        {
            _nmiPending = false;
            _nmiReady = false;
            ServiceInterrupt(NmiVector);
        }
        else if (_irqReady)
        {
            ServiceInterrupt(IrqVector);
        }
        else
        {
            byte opcode = Read(PC++);
            OpcodeInfo info = OpcodeTable.Entries[opcode];
            branchIrq = _irqSample;
            branchNmi = _nmiSample;
            // JSR fetches the high operand byte only after pushing its return address.
            ushort address = info.Op == Op.JSR ? Read(PC++) : Resolve(info.Mode, info.PageCross);
            Execute(info.Op, info.Mode, address);
            pollInterrupts = info.Op != Op.BRK;
            branchPoll = info.Mode == Am.Relative && Cycles - start == 3;
        }

        // A taken branch within a page retains its first-cycle poll; its extra
        // cycle does not poll again. Other instructions use the penultimate
        // cycle (before CLI/SEI/PLP change I, but after RTI pulls P).
        // Interrupt entry itself does not poll: a late NMI waits until the
        // handler's first instruction has executed.
        _irqReady = pollInterrupts && (branchPoll ? branchIrq : _previousIrqSample);
        _nmiReady = pollInterrupts && (branchPoll ? branchNmi : _previousNmiSample) && _nmiPending;
        _stepping = false;

        return (int)(Cycles - start);
    }

    /// <summary>Every clock is a real bus access, including discarded reads.</summary>
    private void ConsumeTick()
    {
        Cycles++;
        OnCycle?.Invoke();
    }

    private void CompleteTick()
    {
        OnCycleComplete?.Invoke();
        bool line = NmiInput?.Invoke() ?? false;
        if (line && !_lastNmiLine) _nmiPending = true;
        _lastNmiLine = line;
        _previousIrqSample = _irqSample;
        _irqSample = _irqLine && (P & FlagInterruptDisable) == 0;
        _previousNmiSample = _nmiSample;
        _nmiSample = _nmiPending;
    }

    private byte Read(ushort address)
    {
        ConsumeTick();
        byte value = _bus.Read(address);
        CompleteTick();
        return value;
    }

    private void Write(ushort address, byte value)
    {
        ConsumeTick();
        _bus.Write(address, value);
        CompleteTick();
    }

    // ----------------------------------------------------------- save states

    internal void SaveState(BinaryWriter writer)
    {
        writer.Write(A);
        writer.Write(X);
        writer.Write(Y);
        writer.Write(S);
        writer.Write(P);
        writer.Write(PC);
        writer.Write(Cycles);
        writer.Write(Jammed);
        writer.Write(_jamPhase);
        writer.Write(_nmiPending);
        writer.Write(_irqLine);
        writer.Write(_baseAddress);
        writer.Write(_irqReady);
        writer.Write(_irqSample);
        writer.Write(_previousIrqSample);
        writer.Write(_nmiReady);
        writer.Write(_nmiSample);
        writer.Write(_previousNmiSample);
        writer.Write(_lastNmiLine);
    }

    internal void LoadState(BinaryReader reader, bool legacy = false)
    {
        A = reader.ReadByte();
        X = reader.ReadByte();
        Y = reader.ReadByte();
        S = reader.ReadByte();
        P = reader.ReadByte();
        PC = reader.ReadUInt16();
        Cycles = reader.ReadInt64();
        Jammed = reader.ReadBoolean();
        _jamPhase = reader.ReadByte();
        _nmiPending = reader.ReadBoolean();
        _irqLine = reader.ReadBoolean();
        _baseAddress = reader.ReadUInt16();
        _irqReady = reader.ReadBoolean();
        _irqSample = reader.ReadBoolean();
        _previousIrqSample = reader.ReadBoolean();
        _nmiReady = reader.ReadBoolean();
        _nmiSample = reader.ReadBoolean();
        _previousNmiSample = reader.ReadBoolean();
        _lastNmiLine = !legacy && reader.ReadBoolean();
    }

    internal void RestoreLegacyNmiInput(bool line, bool pending)
    {
        _lastNmiLine = line;
        _nmiPending |= pending;
    }

    // ------------------------------------------------------------ addressing

    private ushort Resolve(Am mode, bool pageCrossPenalty)
    {
        switch (mode)
        {
            case Am.Implied:
            case Am.Accumulator:
                Read(PC);
                return 0;

            case Am.Immediate:
                return PC++;

            case Am.ZeroPage:
                return Read(PC++);

            case Am.ZeroPageX:
            case Am.ZeroPageY:
            {
                byte pointer = Read(PC++);
                Read(pointer);
                return (byte)(pointer + (mode == Am.ZeroPageX ? X : Y));
            }

            case Am.Relative:
            {
                sbyte offset = (sbyte)Read(PC++);
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
                if (!pageCrossPenalty || CrossesPage(_baseAddress, address))
                {
                    Read((ushort)((_baseAddress & 0xFF00) | (address & 0xFF)));
                }

                return address;
            }

            case Am.AbsoluteY:
            {
                _baseAddress = Read16(PC);
                PC += 2;
                ushort address = (ushort)(_baseAddress + Y);
                if (!pageCrossPenalty || CrossesPage(_baseAddress, address))
                {
                    Read((ushort)((_baseAddress & 0xFF00) | (address & 0xFF)));
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
                byte pointer = Read(PC++);
                Read(pointer);
                return Read16ZeroPage((byte)(pointer + X));
            }

            case Am.IndirectIndexed:
            {
                byte pointer = Read(PC++);
                _baseAddress = Read16ZeroPage(pointer);
                ushort address = (ushort)(_baseAddress + Y);
                if (!pageCrossPenalty || CrossesPage(_baseAddress, address))
                {
                    Read((ushort)((_baseAddress & 0xFF00) | (address & 0xFF)));
                }

                return address;
            }

            default:
                throw new InvalidOperationException($"Unhandled addressing mode {mode}.");
        }
    }

    private ushort Read16(ushort address) =>
        (ushort)(Read(address) | (Read((ushort)(address + 1)) << 8));

    /// <summary>Pointers in the zero page wrap inside it rather than spilling into $0100.</summary>
    private ushort Read16ZeroPage(byte address) =>
        (ushort)(Read(address) | (Read((byte)(address + 1)) << 8));

    /// <summary>
    /// The indirect jump bug. When the pointer ends at $xxFF the processor fetches
    /// the high byte from $xx00 instead of the next page, and shipped games depend
    /// on it, so the bug is part of the contract.
    /// </summary>
    private ushort Read16Wrapped(ushort address)
    {
        ushort high = (ushort)((address & 0xFF00) | (byte)(address + 1));
        return (ushort)(Read(address) | (Read(high) << 8));
    }

    private static bool CrossesPage(ushort from, ushort to) => (from & 0xFF00) != (to & 0xFF00);

    // ------------------------------------------------------------- execution

    private void Execute(Op op, Am mode, ushort address)
    {
        switch (op)
        {
            case Op.ADC: Add(Read(address)); break;
            case Op.SBC: Add((byte)(Read(address) ^ 0xFF)); break;

            case Op.AND: A &= Read(address); SetZeroNegative(A); break;
            case Op.ORA: A |= Read(address); SetZeroNegative(A); break;
            case Op.EOR: A ^= Read(address); SetZeroNegative(A); break;

            case Op.ASL:
                if (mode == Am.Accumulator)
                {
                    A = ShiftLeft(A);
                }
                else
                {
                    Write(address, ShiftLeft(ReadForModify(address)));
                }

                break;

            case Op.LSR:
                if (mode == Am.Accumulator)
                {
                    A = ShiftRight(A);
                }
                else
                {
                    Write(address, ShiftRight(ReadForModify(address)));
                }

                break;

            case Op.ROL:
                if (mode == Am.Accumulator)
                {
                    A = RotateLeft(A);
                }
                else
                {
                    Write(address, RotateLeft(ReadForModify(address)));
                }

                break;

            case Op.ROR:
                if (mode == Am.Accumulator)
                {
                    A = RotateRight(A);
                }
                else
                {
                    Write(address, RotateRight(ReadForModify(address)));
                }

                break;

            case Op.BIT:
            {
                byte value = Read(address);
                SetFlag(FlagZero, (A & value) == 0);
                SetFlag(FlagOverflow, (value & 0x40) != 0);
                SetFlag(FlagNegative, (value & 0x80) != 0);
                break;
            }

            case Op.CMP: Compare(A, Read(address)); break;
            case Op.CPX: Compare(X, Read(address)); break;
            case Op.CPY: Compare(Y, Read(address)); break;

            case Op.DEC:
            {
                byte value = (byte)(ReadForModify(address) - 1);
                Write(address, value);
                SetZeroNegative(value);
                break;
            }

            case Op.INC:
            {
                byte value = (byte)(ReadForModify(address) + 1);
                Write(address, value);
                SetZeroNegative(value);
                break;
            }

            case Op.DEX: X--; SetZeroNegative(X); break;
            case Op.DEY: Y--; SetZeroNegative(Y); break;
            case Op.INX: X++; SetZeroNegative(X); break;
            case Op.INY: Y++; SetZeroNegative(Y); break;

            case Op.LDA: A = Read(address); SetZeroNegative(A); break;
            case Op.LDX: X = Read(address); SetZeroNegative(X); break;
            case Op.LDY: Y = Read(address); SetZeroNegative(Y); break;

            case Op.STA: Write(address, A); break;
            case Op.STX: Write(address, X); break;
            case Op.STY: Write(address, Y); break;

            case Op.TAX: X = A; SetZeroNegative(X); break;
            case Op.TAY: Y = A; SetZeroNegative(Y); break;
            case Op.TXA: A = X; SetZeroNegative(A); break;
            case Op.TYA: A = Y; SetZeroNegative(A); break;
            case Op.TSX: X = S; SetZeroNegative(X); break;
            case Op.TXS: S = X; break;

            case Op.PHA: Push(A); break;
            case Op.PLA: Read((ushort)(StackBase + S)); A = Pull(); SetZeroNegative(A); break;
            case Op.PHP: Push((byte)(P | FlagBreak | FlagUnused)); break;
            case Op.PLP: Read((ushort)(StackBase + S)); P = (byte)((Pull() & ~FlagBreak) | FlagUnused); break;

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
                Read((ushort)(StackBase + S));
                Push16(PC);
                PC = (ushort)(address | (Read(PC) << 8));
                break;

            case Op.RTS:
                Read((ushort)(StackBase + S));
                PC = Pull16();
                Read(PC);
                PC++;
                break;

            case Op.RTI:
                Read((ushort)(StackBase + S));
                P = (byte)((Pull() & ~FlagBreak) | FlagUnused);
                PC = Pull16();
                break;

            case Op.BRK:
                PC++; // The byte after BRK is swallowed.
                Push16(PC);
                Push((byte)(P | FlagBreak | FlagUnused));
                SetFlag(FlagInterruptDisable, true);
                PC = ReadInterruptVector(IrqVector);
                break;

            case Op.NOP:
                // The undocumented multi-byte forms still perform the read, which is
                // why they can cost an extra cycle when indexing crosses a page.
                if (mode != Am.Implied)
                {
                    Read(address);
                }

                break;

            // ------------------------------------------------- undocumented

            case Op.SLO:
            {
                byte value = ShiftLeft(ReadForModify(address));
                Write(address, value);
                A |= value;
                SetZeroNegative(A);
                break;
            }

            case Op.RLA:
            {
                byte value = RotateLeft(ReadForModify(address));
                Write(address, value);
                A &= value;
                SetZeroNegative(A);
                break;
            }

            case Op.SRE:
            {
                byte value = ShiftRight(ReadForModify(address));
                Write(address, value);
                A ^= value;
                SetZeroNegative(A);
                break;
            }

            case Op.RRA:
            {
                byte value = RotateRight(ReadForModify(address));
                Write(address, value);
                Add(value);
                break;
            }

            case Op.SAX: Write(address, (byte)(A & X)); break;

            case Op.LAX:
                // The immediate variant has the same unstable internal mask as XAA.
                A = (byte)(Read(address) & (mode == Am.Immediate ? A | 0xEE : 0xFF));
                X = A;
                SetZeroNegative(A);
                break;

            case Op.DCP:
            {
                byte value = (byte)(ReadForModify(address) - 1);
                Write(address, value);
                Compare(A, value);
                break;
            }

            case Op.ISC:
            {
                byte value = (byte)(ReadForModify(address) + 1);
                Write(address, value);
                Add((byte)(value ^ 0xFF));
                break;
            }

            case Op.ANC:
                A &= Read(address);
                SetZeroNegative(A);
                SetFlag(FlagCarry, (A & 0x80) != 0);
                break;

            case Op.ALR:
                A &= Read(address);
                A = ShiftRight(A);
                break;

            case Op.ARR:
                A &= Read(address);
                A = (byte)((A >> 1) | ((P & FlagCarry) << 7));
                SetZeroNegative(A);
                SetFlag(FlagCarry, (A & 0x40) != 0);
                SetFlag(FlagOverflow, (((A >> 6) ^ (A >> 5)) & 1) != 0);
                break;

            case Op.AXS:
            {
                byte value = Read(address);
                byte and = (byte)(A & X);
                SetFlag(FlagCarry, and >= value);
                X = (byte)(and - value);
                SetZeroNegative(X);
                break;
            }

            case Op.XAA:
                // Unstable silicon behavior: use the 0xEE mask from the NES
                // SingleStepTests model, also used for immediate LAX (0xAB).
                A = (byte)((A | 0xEE) & X & Read(address));
                SetZeroNegative(A);
                break;

            case Op.LAS:
            {
                byte value = (byte)(Read(address) & S);
                A = value;
                X = value;
                S = value;
                SetZeroNegative(value);
                break;
            }

            // The stores below drop the high byte of the target address into the
            // value being written, because the address bus is still driving it.
            case Op.AHX: WriteUnstable(address, (byte)(A & X & HighByteMask())); break;
            case Op.SHY: WriteUnstable(address, (byte)(Y & HighByteMask())); break;
            case Op.SHX: WriteUnstable(address, (byte)(X & HighByteMask())); break;

            case Op.TAS:
                S = (byte)(A & X);
                WriteUnstable(address, (byte)(S & HighByteMask()));
                break;

            case Op.JAM:
                Jammed = true;
                _jamPhase = 0;
                break;

            default:
                throw new InvalidOperationException($"Unhandled operation {op}.");
        }
    }

    private byte HighByteMask() => (byte)((_baseAddress >> 8) + 1);

    private void WriteUnstable(ushort address, byte value)
    {
        if (CrossesPage(_baseAddress, address)) address = (ushort)((value << 8) | (address & 0xFF));
        Write(address, value);
    }

    // -------------------------------------------------------------- helpers

    /// <summary>
    /// Reads the operand of a read-modify-write instruction. The hardware writes
    /// the unchanged value back before writing the new one, and a few registers
    /// react to that first write, so it is reproduced rather than optimised away.
    /// </summary>
    private byte ReadForModify(ushort address)
    {
        byte value = Read(address);
        Write(address, value);
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

        Read(PC);
        if (CrossesPage(PC, target))
        {
            Read((ushort)((PC & 0xFF00) | (target & 0xFF)));
        }

        PC = target;
    }

    private void ServiceInterrupt(ushort vector)
    {
        Read(PC);
        Read(PC);
        Push16(PC);
        Push((byte)((P | FlagUnused) & ~FlagBreak));
        SetFlag(FlagInterruptDisable, true);
        PC = ReadInterruptVector(vector);
    }

    private ushort ReadInterruptVector(ushort vector)
    {
        // Vector selection uses the NMI sample from before the status push.
        // An edge in the first four entry cycles can hijack IRQ/BRK without
        // changing its return address or B bit; a later edge stays pending.
        if (_previousNmiSample && _nmiPending)
        {
            vector = NmiVector;
            _nmiPending = false;
            _nmiReady = false;
        }
        return Read16(vector);
    }

    // DMA also owns real bus cycles, with all chips continuing to tick.
    internal byte DmaRead(ushort address) => Read(address);
    internal void DmaWrite(ushort address, byte value) => Write(address, value);

    private void Push(byte value)
    {
        Write((ushort)(StackBase + S), value);
        S--;
    }

    private byte Pull()
    {
        S++;
        return Read((ushort)(StackBase + S));
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
