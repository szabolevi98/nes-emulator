# Cycle-accuracy work log

The working target is the NTSC Ricoh 2A03/2C02 console with the supported cartridge boards. Clock alignment and chip revisions are part of that target: the alternate MMC3A and the unstable CPU opcodes cannot be treated as interchangeable with every other hardware variant. A finite ROM suite measures the behavior it exercises; passing it is not a proof that every hardware interaction is implemented.

## Vblank, NMI and odd frames

The first timing milestone brings `ppu_vbl_nmi` from 4/10 to **10/10**, and the full public suite from 38/55 to **44/55**, with no previously passing ROM lost.

The CPU cycle now has a before-access and an after-access phase. For the selected NTSC alignment, two PPU dots run before the bus read/write and one runs after it. The CPU samples interrupt inputs after that final dot. Every CPU cycle still performs the same bus access; the instruction API and cycle totals are unchanged.

The PPU exposes the logical assertion of its NMI output (`vblank flag AND NMI enable`) instead of handing the CPU an irrevocable event on every PPU tick. The CPU samples and edge-detects that input. This lets a short assertion disappear before sampling, while an already captured edge survives a subsequent status read. Re-enabling NMI during vblank can create another edge, but holding the line asserted cannot retrigger it.

A `$2002` read immediately before the vblank set suppresses that frame's flag and NMI. The emulator's `Cycle` property names the *next* dot to execute, so this boundary is scanline 241, `Cycle == 1`, after dot 0 has executed. The adjacent status and NMI cases are tested through real CPU bus reads, not just by inspecting PPU flags.

Odd frames skip the final pre-render dot, with render enable latched on the preceding dot. Reads and writes on both sides of that boundary are covered by `10-even_odd_timing` and offline tests, including a save/load at the boundary.

References: [PPU frame timing](https://www.nesdev.org/wiki/PPU_frame_timing), [NMI operation](https://www.nesdev.org/wiki/NMI), and the pinned [test ROM sources](https://github.com/christopherpow/nes-test-roms/tree/95d8f621ae55cee0d09b91519a8989ae0e64753b/ppu_vbl_nmi/source). Other power-up clock alignments remain outside this milestone.

## APU frame-counter timing

The second milestone brings `apu_test` from 4/8 to **7/8**. Correcting the frame IRQ timing also makes `cpu_interrupts_v2/4-irq_and_dma` pass, so the complete suite reaches **48/55**, retaining every previous pass. This result covers OAM DMA and frame IRQ interaction; DMC bus arbitration remains a separate target.

A `$4017` write updates the mode and IRQ-inhibit bits immediately, but resets the sequencer on the second GET cycle after the write: three CPU cycles after PUT, four after GET. The old sequence continues during that delay. In five-step mode the reset also clocks the quarter- and half-frame units. The four-step sequence lasts 29,830 CPU cycles and asserts its IRQ latch on three consecutive cycles; reading `$4015` between assertions clears the latch until the next assertion. The five-step sequence lasts 37,282 CPU cycles.

The public `4-jitter`, `5-len_timing` and `6-irq_flag_timing` ROMs exercise both clock phases, all four length counters and consecutive IRQ assertions. Offline checks also cover replacement of a pending reset, two complete five-step sequences, and saving immediately before a delayed reset.

References: [APU frame counter](https://www.nesdev.org/wiki/APU_Frame_Counter), [hardware confirmation of the 3/4-cycle write delay](https://forums.nesdev.org/viewtopic.php?t=26816), and the pinned [APU test sources](https://github.com/christopherpow/nes-test-roms/tree/95d8f621ae55cee0d09b91519a8989ae0e64753b/apu_test/source).

## Validation and save compatibility

- 284 offline checks, including status/NMI reads across five adjacent dots, repeated NMI edges, odd-frame boundaries, APU reset and IRQ boundaries, and real v2/v3 state migration fixtures.
- The complete public ROM report retains all seven failures and their messages.
- The independent CPU vector suite checks registers, memory and every bus operation for all 256 opcodes.
- Local Mega Man 4 and Super Mario Bros. 3 runs exercise game input, rendering and audio; their ROMs and generated captures remain outside version control.

New saves use format v4 to preserve the pending APU reset delay as well as the CPU/PPU timing latches introduced in v3. Existing v2 and v3 saves remain loadable, with no pending APU reset. For v2, migration seeds the CPU's sampled NMI level from the restored PPU and transfers any old pending PPU event, avoiding a duplicate NMI from a held line or loss of an unconsumed event.

```powershell
dotnet run -c Release --project tests/NesEmulator.Tests
dotnet run -c Release --project tests/NesEmulator.Tests -- --rom-suite roms/accuracy docs/accuracy-results.md
dotnet run -c Release --project tests/NesEmulator.Tests -- --cpu-vectors roms/cpu-vectors
```

## Next targets

1. CPU NMI/BRK/IRQ overlap and branch interrupt polling.
2. DMC prefetch and CPU/OAM DMA bus arbitration.
3. MMC3 A12 fetch timing and an explicit MMC3A revision option.
4. Per-dot sprite evaluation and the hardware overflow behavior, with additional public suites.

Each milestone should retain the previous passing checks and report its remaining mismatches. Sprite evaluation and DMA interactions need coverage beyond the present 55-ROM set.
