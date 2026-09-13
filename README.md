# NES Emulator

A Nintendo Entertainment System emulator written in C#, built around a cycle counted 6502 core, a picture unit that follows the beam and a sound unit with the real non-linear mixer.

![Super Mario Bros. 3 running in the emulator](docs/screenshot.png)

Super Mario Bros. 3 on an MMC3 board: four switchable program slots, eight tile banks, and the line counter that splits the screen.

The console is a small, completely documented machine, which makes it an unusually honest thing to build: there is a right answer for every instruction, and published test programs exist to say whether you got it. This repository aims at those answers rather than at a screenshot that looks close enough — including where the answer is still no.

## What works

All three chips run, and commercial cartridges render and play.

- **6502 core** — every documented instruction plus the undocumented opcodes, driven by the bus: each cycle performs a real read or write, dummy reads included
- **Picture unit** — background and sprites, scrolling, palettes, sprite zero hit, sprite overflow, edge clipping, drawn one pixel per cycle as the beam sweeps
- **Sound unit** — two square waves with sweeps, triangle, noise and sample playback through the console's non-linear mixer, band limited before it reaches the sound card
- **Cartridges** — iNES parsing and mappers 0, 1, 2, 3 and 4, which between them cover most of the library; the MMC3 follows filtered A12 edges for its line counter
- **Save states and rewind** — a full console state, about five kilobytes compressed, and a minute of play to wind back through
- **Debugger** — a disassembler and execution trace that folds out beside the screen

How the interesting parts work, and which hardware quirks had to be reproduced rather than fixed, is in [how it works](docs/how-it-works.md).

## Accuracy

Two things are measured here. One is a suite of 415 offline checks. The other is the public test ROMs, which probe cycle-exact edges rather than just checking whether games run. They provide a reproducible measure of timing accuracy for specific hardware behavior. The full vblank/NMI, CPU interrupt and APU suites now pass.

| Measure | Passed | What it covers |
|---|---:|---|
| **CPU bus-cycle vectors** | **2,560,000 / 2,560,000** | Every one of the 256 opcodes: registers, memory, cycle counts, and each bus address, value and direction |
| **Processor behaviour and instruction timing** | **25 / 26** | Instruction behaviour, timing, page wrapping, dummy reads and writes, reset |
| Interrupt and video edge timing | 20 / 21 | NMI/BRK/IRQ overlap, vblank suppression, MMC3 scanline timing |
| Sound unit timing | 8 / 8 | Frame-counter sequencing and the DMC prefetch buffer |
| **Public ROM total** | **53 / 55** | |
| Additional DMA suite | 2 / 2 | DMC/OAM collisions, transfer length and copied sprite data |

Of the two remaining baseline failures, one exercises the alternate MMC3A board revision, and one is a deliberate disagreement: for the unstable `$AB` opcode this emulator follows the `$EE` mask used by the [SingleStepTests vectors](https://github.com/SingleStepTests/65x02/tree/2f6980a2d95757486c7bee24355c360e40e2a224/nes6502), while blargg's ROM assumes a different result. The discrepancy is reported rather than hidden by skipping the opcode. Further DMA edge cases and per-dot sprite evaluation remain beyond these suites; the separate [DMA report](docs/dma-results.md) keeps the original 55-ROM denominator stable.

Every result, failure message and ROM checksum is in the [full report](docs/accuracy-results.md), and the per-opcode vector results in the [CPU vector report](docs/cpu-vector-results.txt). Measured on Windows x64 with .NET 9 on 2026-09-13. The [cycle-accuracy work log](docs/cycle-accuracy.md) records the clock model, completed checks and next targets.

## Tests

```
dotnet run --project tests/NesEmulator.Tests      # 415 offline checks
dotnet run --project tests/NesEmulator.UiTests    # 80 keyboard and focus checks
```

```
PASS  indirect jump reproduces the page wrap bug
PASS  absolute,X pays for crossing a page
PASS  rmw: performs the hardware double write
PASS  ppu: a frame is 89,342 cycles
PASS  render: the leftmost squares are clipped away
PASS  mmc3: the interrupt arrives on the counted line
PASS  state: replaying from a state is deterministic
...
415/415 passed
```

They cover the opcode table, every addressing mode, the signed overflow cases, branch and interrupt timing, all five mappers, the picture unit registers and mirroring, the sound unit down to its envelopes, the save state round trip and the rewind ring — and, end to end, a small program that writes a palette and a name table and is then checked pixel by pixel against what came out. Nothing is downloaded.

To reproduce the public ROM runs, fetch the pinned test data first:

```powershell
./tools/fetch-accuracy-tests.ps1
dotnet run -c Release --project tests/NesEmulator.Tests -- --rom-suite roms/accuracy docs/accuracy-results.md
dotnet run -c Release --project tests/NesEmulator.Tests -- --dma-suite roms/accuracy docs/dma-results.md
```

The bus-cycle vectors are an optional download of roughly 1 GB:

```powershell
./tools/fetch-accuracy-tests.ps1 -IncludeCpuVectors
dotnet run -c Release --project tests/NesEmulator.Tests -- --cpu-vectors roms/cpu-vectors
```

## Running it

```
dotnet build NesEmulator.sln
dotnet run --project src/NesEmulator
```

Open a cartridge with **File → Open ROM**, or drop one on the window. `roms/demo.nes` is included: a handwritten cartridge that fills a screen with tiles, scrolls it from the frame interrupt, and sweeps a square wave in step with the scroll so there is something to hear as well. `tools/make-demo-rom.sh` builds it, with the full source listed in its comments.

![The demo cartridge](docs/demo.png)

| Control | Keys |
|---|---|
| Player one | Arrows = directions, X = A, Z = B, Enter = Start, Shift = Select |
| Player two | WASD = directions, G = A, F = B, R = Start, T = Select |
| Save / load state | F1 / F4 |
| Rewind | Hold Backspace |
| Pause | F5 |
| Reset | Ctrl+R |
| Mute / unmute | M |
| Debugger | F12 |

**Help → Controls** lists the same bindings in the app. Controller keys work even when a debugger control has focus; switching away from the emulator releases both controllers so buttons cannot stick.

For a copy that runs on a machine with no .NET installed:

```
dotnet publish src/NesEmulator/NesEmulator.csproj -p:PublishProfile=win-x64-single-file
```

That leaves one 49 MB `publish/NesEmulator.exe`, which also accepts a cartridge path on the command line. Building needs the .NET 9 SDK; the shell needs Windows, while the core targets plain `net9.0` and has no platform dependencies.

**No commercial ROMs are included and none will be.** Anything dropped into `roms/` stays out of version control apart from the demo cartridge.

## Layout

- `src/NesEmulator.Core/` — the console, with no user interface dependencies, so the tests can run it headless
  - `Cpu/` — the opcode table, the processor and the disassembler
  - `Ppu/` — the picture unit and the colour table
  - `Apu/` — the sound unit, its five channels and the resampler
  - `Cartridges/` — iNES parsing and the mappers
  - `Memory/` · `Input/` · `RewindBuffer.cs`
- `src/NesEmulator/` — the Windows Forms shell and the wave output
- `tests/` — the console test runner and the Windows input checks
- `tools/` — the demo cartridge generator and the test-data fetcher

## License

MIT. See [LICENSE](LICENSE).
