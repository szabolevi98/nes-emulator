# NES Emulator

A Nintendo Entertainment System emulator written in C#, built around a cycle counted 6502 core and verified against the hardware behaviour that games actually depend on.

The console is a small, completely documented machine, which makes it an unusually honest thing to build: there is a right answer for every instruction, and published test programs exist to say whether you got it. This repository aims at those answers rather than at a screenshot that looks close enough.

## Where it is

The processor is finished and tested. The picture and sound units are not written yet, so there is no video output — what runs today is the debugger shell around the core.

- [x] **6502 core** — all 151 documented instructions plus the undocumented opcodes, with per instruction cycle counts
- [x] **Cycle accuracy** — page crossing penalties, branch penalties, and the double write that read-modify-write instructions perform
- [x] **Interrupts** — reset, non-maskable and maskable, with the correct vectors, stack layout and break flag handling
- [x] **iNES cartridge parsing** — header, trainer, program and character banks, mirroring, battery and NES 2.0 detection
- [x] **Mapper 0 (NROM)** — the plain board behind Super Mario Bros, Donkey Kong and Duck Hunt
- [x] **Disassembler and execution trace**
- [ ] Picture unit: background rendering, sprites, scrolling
- [ ] Sound unit: two pulse channels, triangle, noise, sample playback
- [ ] Controller input
- [ ] Mappers 1, 2, 3 and 4, which together cover most of the library
- [ ] Save states and rewind

## The parts that are easy to get wrong

Writing a processor is mostly transcription. The interesting work is in the handful of places where the real chip does something a clean implementation would not, and where games came to rely on exactly that:

- **The indirect jump bug.** `JMP ($30FF)` reads its high byte from `$3000`, not `$3100`, because the increment never carries into the high half of the address. Shipped games depend on this, so it is part of the contract rather than a defect to fix.
- **Page crossing costs a cycle, but only for reads.** `LDA $80FF,X` takes a fifth cycle when the index pushes it into the next page, because the processor has to correct the address it already guessed. A store takes the long path every time, so its cost never changes.
- **Read-modify-write writes twice.** `INC $20` puts the unmodified value back before it writes the new one. On work RAM nothing notices, but some hardware registers react to the first write, and a version that optimises it away behaves differently on real games.
- **The break flag is not a real flag.** Nothing stores it in the status register; it only exists in the copy pushed onto the stack, and its value says whether the push came from an instruction or from an interrupt.
- **Decimal mode is fused off.** The 2A03 is a 6502 with binary coded decimal disabled, so `SED` sets a flag that changes nothing about how `ADC` adds.

Each of those has a test that fails if the shortcut is taken instead.

## Tests

The suite is a dependency free console runner that prints one line per check, matching the style used across these projects:

```
dotnet run --project tests/NesEmulator.Tests
```

```
PASS  indirect jump reproduces the page wrap bug
PASS  absolute,X pays for crossing a page
PASS  stores do not pay the crossing penalty
PASS  rmw: performs the hardware double write
...
82/82 passed
```

It covers the opcode table itself, every addressing mode including the zero page wraps, the signed overflow cases for addition and subtraction, branch and interrupt timing, the stack and return instructions, the undocumented opcodes, cartridge parsing and the address space mirroring.

Once the picture unit exists, the published test ROMs join this list and the results table moves into this README, which is the measure that actually matters for an emulator.

## Trying it

```
dotnet build NesEmulator.sln
dotnet run --project src/NesEmulator
```

`roms/demo.nes` is a handwritten cartridge that loops forever, so the trace window has something to show without needing a commercial ROM. `tools/make-demo-rom.sh` builds it and lists its source.

Open a cartridge with **File → Open ROM**, then step through it or run a thousand instructions at a time:

```
C000  A9 00     LDA #$00     A:00 X:00 Y:00 P:24 SP:FD CYC:7
C002  A2 00     LDX #$00     A:00 X:00 Y:00 P:26 SP:FD CYC:9
C004  E8        INX          A:00 X:00 Y:00 P:26 SP:FD CYC:11
C005  18        CLC          A:00 X:01 Y:00 P:24 SP:FD CYC:13
C006  69 03     ADC #$03     A:00 X:01 Y:00 P:24 SP:FD CYC:15
C008  4C 04 C0  JMP $C004    A:03 X:01 Y:00 P:24 SP:FD CYC:17
```

## Layout

- `src/NesEmulator.Core/` — the console itself, with no user interface dependencies, so the tests can run it headless
  - `Cpu/` — the opcode table, the processor and the disassembler
  - `Cartridges/` — iNES parsing and the mapper implementations
  - `Memory/` — the processor address space
- `src/NesEmulator/` — the Windows Forms debugger shell
- `tests/NesEmulator.Tests/` — the console test runner
- `tools/` — the demo cartridge generator

`Cpu/OpcodeTable.cs` holds all 256 entries written out in full, four to a line, so a row matches a row of the published opcode matrix and can be checked against it by eye. Writing it as data rather than as a switch is also what makes the disassembler nearly free.

## ROMs

No commercial ROMs are included and none will be; they are copyrighted. The emulator is tested with `roms/demo.nes` and, as the hardware emulation grows, with the freely distributed accuracy test ROMs. Anything you drop into `roms/` stays out of version control apart from the demo cartridge.

## Requirements

.NET 9 SDK, Windows for the shell. The core targets plain `net9.0` and has no platform dependencies.

## License

MIT. See [LICENSE](LICENSE).
