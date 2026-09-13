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

## CPU interrupt entry and branch polling

The third milestone brings `cpu_interrupts_v2` from 2/5 to **5/5** and the complete suite to **51/55**, with no previous pass lost. All 2,560,000 CPU bus-cycle vectors also pass after this change.

A taken branch within the same page keeps the interrupt poll from its opcode-fetch cycle. Its extra cycle does not poll again. A page-crossing branch uses the later poll, while an untaken branch keeps its ordinary two-cycle timing. This applies to both IRQ and NMI.

An NMI sampled during the first four cycles of BRK or IRQ entry redirects both vector reads to the NMI vector, preserving the original return address and stacked B flag. The selection uses the sample from before the status push. A later NMI cannot change either vector byte and waits until the handler's first instruction has executed: interrupt entry does not perform an ordinary instruction-end poll.

Offline tests sweep NMI assertion across all seven BRK/IRQ entry cycles, checking the bus addresses and directions, stack contents, first handler instruction and save/load at the entry boundary. Separate IRQ and NMI sweeps cover untaken, taken and page-crossing branches, with JMP as a control. These cover sustained assertions; sub-cycle pulses and the transistor-level lost-NMI window need additional coverage.

References: [CPU interrupt polling](https://www.nesdev.org/wiki/Interrupts), [Visual6502 interrupt hijacking](https://www.nesdev.org/wiki/Visual6502wiki/6502_Interrupt_Hijacking), and the pinned [CPU interrupt test sources](https://github.com/christopherpow/nes-test-roms/tree/95d8f621ae55cee0d09b91519a8989ae0e64753b/cpu_interrupts_v2/source).

## MMC3 and the aborted PPU fetch

The fourth milestone makes `mmc3_test_2/4-scanline_timing` pass, bringing that group to **5/6** and the full public suite to **52/55**. The remaining MMC3 ROM targets the alternate IRQ-counter revision.

The last PPU dot drives a background pattern address without completing a memory read. This aborted fetch was missing: with backgrounds in `$1000`, A12 stayed low long enough to count an extra edge at the next line's first pattern fetch. The mapper now sees that address, but its CHR read handler is not called. The odd pre-render skip naturally omits the aborted fetch. The redundant nametable accesses are at dots 337 and 339, followed by the next line's first nametable access at dot 1.

These positions use the emulator's existing state-machine dot numbering, in which the skipped dot is the final pre-render dot. The aborted access is also described as the next line's idle dot in descriptions that number the external PPU signals one dot later. The public ROM checks IRQ timing on scanlines 0, 1 and 239 in both pattern-table configurations.

Offline bus traces distinguish address changes from CHR reads. Whole-frame checks exercise background-only, sprite-only and combined rendering across even and odd frames. The MMC3 filter still approximates three M2 falling edges as an eight-PPU-dot low interval; exact M2 phase tracking remains future work.

References: [PPU rendering](https://www.nesdev.org/wiki/PPU_rendering), [hardware explanation of the aborted fetch and MMC3](https://forums.nesdev.org/viewtopic.php?t=25255), and the pinned [scanline timing test](https://github.com/christopherpow/nes-test-roms/blob/95d8f621ae55cee0d09b91519a8989ae0e64753b/mmc3_test_2/source/4-scanline_timing.s).

## DMC prefetch and shared DMA arbitration

The fifth milestone makes `apu_test/7-dmc_basics` pass: **8/8 APU ROMs**, **53/55 baseline ROMs**, and **2/2 additional DMA ROMs**. The separate `--dma-suite` runs both `sprdma_and_dmc_dma` ROMs, which check transfer duration and OAM contents across collisions. The original baseline keeps its 55-ROM denominator.

The DMC memory reader fills a one-byte buffer independently of the output shifter. Fetching the final byte updates active/IRQ status or restarts the loop immediately; the buffered byte and current output bits remain playable after disabling the reader. Sample addresses wrap from `$FFFF` to `$8000`.

DMA now holds the CPU at its next eligible read, including reads inside an instruction. Writes continue until a read permits the halt. Initial DMC loads wait for the second following GET phase; later reloads request a PUT halt. Halt, dummy, alignment and transfer cycles all clock the CPU, APU and PPU. DMC reads win over OAM reads, while preparation cycles overlap the ongoing OAM transfer. An instruction's own bus-cycle count remains separate from stolen cycles so a stalled branch keeps its early interrupt poll.

On the selected NES-001 model, contiguous halted controller reads keep /OE asserted and clock the pad once. Standard pads now shift in ones after their eight buttons. The older `dmc_dma_during_read4/dma_4016_read` ROM has no `$6000` result protocol, so it is not included in the automated table; a local run's nametable text reports `08 08 07 08 08` and `Passed`, matching its source. Reset cancels pending DMA requests and preserves the underlying clock phase.

The offline checks cover load alignment, consecutive CPU writes, OAM byte order, controller reads, buffer retention, address wrap, an IRQ arriving during a stalled branch, reset, and deterministic saves with pending or active DMC playback. DMA completes synchronously inside a CPU read, so public instruction-boundary saves need the pending request and channel state, not a partially executed arbitration loop.

References: [DMC reader and output behavior](https://www.slack.net/~ant/nes-emu/dmc/), [DMA cycles and collisions](https://www.nesdev.org/wiki/DMA), and [hardware OAM/DMC test results](https://forums.nesdev.org/viewtopic.php?t=6100).

## Validation and save compatibility

- 415 offline checks, including CPU/PPU/APU timing, DMA arbitration and real v2/v3/v4 state migration fixtures.
- The complete baseline ROM report retains both failures and their messages; DMA results are reported separately.
- The independent CPU vector suite checks registers, memory and every bus operation for all 256 opcodes.
- Local Mega Man 4 and Super Mario Bros. 3 runs exercise game input, rendering and audio; their ROMs and generated captures remain outside version control.

New saves use format v5, adding the DMC buffer, pending DMA delay and GET/PUT phase to the v4 APU reset delay and v3 CPU/PPU timing latches. Existing v2/v3/v4 saves remain loadable. Older DMC states retain their output shifter and unread sample address, start with an empty prefetch buffer, and schedule a fetch if the reader is active. v2/v3 states have no pending APU reset. For v2, migration also seeds the CPU's sampled NMI level and transfers any pending PPU NMI event.

```powershell
dotnet run -c Release --project tests/NesEmulator.Tests
dotnet run -c Release --project tests/NesEmulator.Tests -- --rom-suite roms/accuracy docs/accuracy-results.md
dotnet run -c Release --project tests/NesEmulator.Tests -- --dma-suite roms/accuracy docs/dma-results.md
dotnet run -c Release --project tests/NesEmulator.Tests -- --cpu-vectors roms/cpu-vectors
```

## Next targets

1. Remaining DMA quirks: stop/abort windows, hybrid `$4000–$401F` register selection during DMA, and adjacent PPUDATA-read behavior. These are not established by the current passing suites.
2. An explicit MMC3A revision option and exact M2-phase filtering of A12.
3. Per-dot sprite evaluation and the hardware overflow behavior, with additional public suites.

Each milestone should retain the previous passing checks and report its remaining mismatches. Sprite evaluation and DMA interactions need coverage beyond the present 55-ROM set.
