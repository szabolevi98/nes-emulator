# NES Emulator

A Nintendo Entertainment System emulator written in C#, built around a cycle counted 6502 core and a picture unit that follows the beam, verified against the hardware behaviour that games actually depend on.

![demo.nes running](docs/demo.png)

The console is a small, completely documented machine, which makes it an unusually honest thing to build: there is a right answer for every instruction, and published test programs exist to say whether you got it. This repository aims at those answers rather than at a screenshot that looks close enough.

## Where it is

The processor and the picture unit both work, and a cartridge renders. Sound is not written yet.

- [x] **6502 core** — all 151 documented instructions plus the undocumented opcodes, with per instruction cycle counts
- [x] **Cycle accuracy** — page crossing penalties, branch penalties, and the double write that read-modify-write instructions perform
- [x] **Interrupts** — reset, non-maskable and maskable, with the correct vectors, stack layout and break flag handling
- [x] **Picture unit** — background and sprite rendering, scrolling, palettes, sprite zero hit, sprite overflow, edge clipping
- [x] **Sprite memory transfer** — the page copy through $4014, with the processor held still for it
- [x] **Controllers** — both ports, as the serial shift registers they are
- [x] **Cartridges** — iNES parsing, and mappers 0, 1, 2 and 3, which between them cover most of the library
- [x] **Disassembler and execution trace**, in a debugger panel beside the screen
- [ ] Sound unit: two pulse channels, triangle, noise, sample playback
- [ ] Mapper 4, the MMC3, with its scanline counter
- [ ] Save states and rewind
- [ ] A per-cycle processor and picture unit interleave

## The parts that are easy to get wrong

Writing a processor is mostly transcription. The interesting work is in the handful of places where the real chip does something a clean implementation would not, and where games came to rely on exactly that:

- **The indirect jump bug.** `JMP ($30FF)` reads its high byte from `$3000`, not `$3100`, because the increment never carries into the high half of the address. Shipped games depend on this, so it is part of the contract rather than a defect to fix.
- **Page crossing costs a cycle, but only for reads.** `LDA $80FF,X` takes a fifth cycle when the index pushes it into the next page, because the processor has to correct the address it already guessed. A store takes the long path every time, so its cost never changes.
- **Read-modify-write writes twice.** `INC $20` puts the unmodified value back before it writes the new one. On work RAM nothing notices, but some hardware registers react to the first write, and a version that optimises it away behaves differently on real games.
- **The break flag is not a real flag.** Nothing stores it in the status register; it only exists in the copy pushed onto the stack, and its value says whether the push came from an instruction or from an interrupt.
- **Decimal mode is fused off.** The 2A03 is a 6502 with binary coded decimal disabled, so `SED` sets a flag that changes nothing about how `ADC` adds.

Each of those has a test that fails if the shortcut is taken instead.

## Following the beam

The picture unit does not draw a frame and hand it over. It produces one pixel per cycle while a television beam sweeps the screen, 341 cycles across and 262 lines down, three of its cycles for every processor cycle. Games use that: they change the scroll position partway down a frame so a status bar stays still while the level moves underneath it, and they find the right moment by watching for the sprite zero hit flag, which goes up the instant an opaque sprite pixel lands on an opaque background pixel.

So the renderer follows the beam rather than walking tiles. Its scroll position lives in two fifteen bit registers, packed as fine y, name table, coarse y, coarse x, and the bit manipulation in `Ppu/Ppu2C02.cs` is that layout being incremented and copied the way the hardware does it — including coarse y counting to 29 rather than 31, because the last two rows of a name table hold attribute bytes rather than tiles.

Two kilobytes of name table memory has to cover four screens, so the cartridge wires the same memory into two of the four slots. Which two is what mirroring means, and it is why the demo above scrolls sideways forever across a single screen of tiles.

## Tests

The suite is a dependency free console runner that prints one line per check, matching the style used across these projects:

```
dotnet run --project tests/NesEmulator.Tests
```

```
PASS  indirect jump reproduces the page wrap bug
PASS  absolute,X pays for crossing a page
PASS  rmw: performs the hardware double write
PASS  ppu: $3F10 and $3F00 are the same byte
PASS  ppu: a frame is 89,342 cycles
PASS  render: the tile is drawn in its palette colour
PASS  render: the leftmost squares are clipped away
...
114/114 passed
```

It covers the opcode table itself, every addressing mode including the zero page wraps, the signed overflow cases for addition and subtraction, branch and interrupt timing, the stack and return instructions, the undocumented opcodes, cartridge parsing, all four mappers, the picture unit registers and mirroring, the sprite memory transfer, the controllers — and, end to end, a small program that writes a palette and a name table and is then checked pixel by pixel against what came out.

The published accuracy test ROMs are the next measure, and their results table belongs in this README once they run.

## Trying it

```
dotnet build NesEmulator.sln
dotnet run --project src/NesEmulator
```

Open a cartridge with **File → Open ROM**, or drop one on the window. `roms/demo.nes` is included and is what the picture above shows: a handwritten cartridge that fills a screen with tiles and scrolls it, driven by the frame interrupt. `tools/make-demo-rom.sh` builds it, with the full source listed in its comments.

| | |
|---|---|
| D-pad | Arrow keys |
| A / B | X / Z |
| Start / Select | Enter / Shift |
| Pause | F5 |
| Reset | Ctrl+R |
| Debugger | F12 |

The debugger panel folds out beside the screen and shows the register file, the beam position and the same instruction trace the processor was built against.

## Release build

The release artifact is a single self contained executable that runs without a .NET installation. The settings live in a publish profile, so Visual Studio and the command line produce the same file:

```
dotnet publish src/NesEmulator/NesEmulator.csproj -p:PublishProfile=win-x64-single-file
```

That leaves one 49 MB `publish/NesEmulator.exe`, which can be copied anywhere and double clicked. It also accepts a cartridge path on the command line:

```
NesEmulator.exe roms\demo.nes
```

## Layout

- `src/NesEmulator.Core/` — the console itself, with no user interface dependencies, so the tests can run it headless
  - `Cpu/` — the opcode table, the processor and the disassembler
  - `Ppu/` — the picture unit and the colour table
  - `Cartridges/` — iNES parsing and the mappers
  - `Memory/` — the processor address space
  - `Input/` — the controllers
- `src/NesEmulator/` — the Windows Forms shell
- `tests/NesEmulator.Tests/` — the console test runner
- `tools/` — the demo cartridge generator

`Cpu/OpcodeTable.cs` holds all 256 entries written out in full, four to a line, so a row matches a row of the published opcode matrix and can be checked against it by eye. Writing it as data rather than as a switch is also what makes the disassembler nearly free.

## Known limits

The processor is allowed to finish an instruction before the picture unit is caught up to it. That is accurate at instruction boundaries but not inside one, so a game polling a picture register in a tight loop can see a change a few cycles later than hardware would show it. Moving to a per-cycle interleave is what fixes it, and it is on the list above.

## ROMs

No commercial ROMs are included and none will be; they are copyrighted. The emulator is tested with `roms/demo.nes` and, as the hardware emulation grows, with the freely distributed accuracy test ROMs. Anything you drop into `roms/` stays out of version control apart from the demo cartridge.

## Requirements

.NET 9 SDK, Windows for the shell. The core targets plain `net9.0` and has no platform dependencies.

## License

MIT. See [LICENSE](LICENSE).
