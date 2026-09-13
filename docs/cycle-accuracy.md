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

## Selectable MMC3 IRQ revisions

The sixth milestone supports alternate MMC3A/non-Sharp MMC3B IRQ behavior alongside the existing standard profile. The public suite reaches **54/55**, including **6/6 MMC3 ROMs**, with the revision explicitly recorded for each test. `5-MMC3` and `6-MMC3_alt` require incompatible hardware behavior: only the latter selects the alternate profile. This is test configuration in the runner, not ROM-name detection in the core or a change to the ROM bytes.

Both profiles assert when the counter decrements to zero, or an explicit `$C001` request reloads zero. Only the standard profile also asserts on a natural zero-to-zero reload. Reload writes do not assert immediately, counting continues while IRQs are disabled, and a pending IRQ remains latched until acknowledged. The existing A12 filter is unchanged.

The core accepts `mmc3Revision: Mmc3IrqRevision.Alternate` when constructing a console or calling `Nes.FromFile`. The desktop exposes the same selection under **Emulation → MMC3 IRQ revision (restarts ROM)** for mapper 4 cartridges. It recreates the console and rewind buffer, preserves pause, and shows the selected profile in the status bar. Opening a ROM defaults to standard behavior; automatic chip-revision detection is not implemented.

Offline checks cover both IRQ truth tables through qualified and rejected A12 edges, latching, counting while disabled, deterministic save replay, and refusal of mismatched state revisions before mutation. Desktop tests exercise both menu choices, restart and pause behavior. A real v5 MMC3/DMC fixture checks migration against bytes from the previous serializer.

A separate cross-profile run also passes MMC3 tests 1–4 with the alternate profile. As expected, `5-MMC3` returns failure 2 under alternate hardware and `6-MMC3_alt` returns failure 2 under standard hardware. These negative controls are not added to the 55-ROM total.

References: [MMC3 IRQ revisions](https://www.nesdev.org/wiki/MMC3#IRQ_Specifics) and the pinned [alternate-revision test source](https://github.com/christopherpow/nes-test-roms/blob/95d8f621ae55cee0d09b91519a8989ae0e64753b/mmc3_test_2/source/6-MMC3_alt.s).

## MMC3 A12 filtering on actual M2 edges

The seventh milestone replaces the fixed eight-dot low-time threshold with a saturating count of three falling CPU M2 edges. Any high A12 pulse clears the filter immediately, including pulses between M2 edges. Repeated low addresses preserve its progress. The counter still clocks on the qualifying A12 rise, not on M2 itself.

The console delivers M2's falling edge after the third PPU dot of each CPU bus cycle and before CPU interrupt sampling, retaining the selected NTSC alignment. The same path runs during CPU reads, writes, reset and both DMA units. Advancing the PPU in isolation no longer invents CPU edges. The PPU's elapsed clock remains monotonic across reset and odd-frame skips; its scanline/dot coordinates are separate.

Low intervals spanning 7, 8 or 9 dots now behave differently across the three whole-dot M2 alignments. The offline pulse sweep covers those boundaries for both IRQ revisions. Whole-frame traces cover both pattern-table configurations, background-only/sprite-only/combined rendering, even/odd frames, and all three alignments. In the background-at-`$1000` configuration, the skipped pre-render fetch yields an extra first-line clock in two alignments, rather than unconditionally as the old threshold did.

The public result remains **54/55 ROMs**, including **6/6 MMC3** and **2/2 additional DMA ROMs**. These ROMs alone do not distinguish every M2 alignment; the new offline traces cover that gap. Sub-dot propagation delays and other power-up alignments remain outside the selected clock model.

References: [MMC3 IRQ specifics](https://www.nesdev.org/wiki/MMC3#IRQ_Specifics) and [hardware discussion of three M2 falls and the odd-frame alignment](https://forums.nesdev.org/viewtopic.php?start=30&t=24229).

## Per-dot sprite evaluation and overflow

The eighth milestone brings the additional overflow suite from **3/5 to 5/5**: `3.Timing` and `4.Obscure` now pass. All **11/11 sprite-zero-hit ROMs** also pass, preserving their earlier results. The separate `--sprite-suite` reports these **16/16** results without changing the baseline denominator.

The PPU now has a 32-byte secondary OAM distinct from the current line's output units. Visible-line dots 1–64 clear one byte on each even dot. During dots 65–256, odd dots read primary OAM and even dots compare/copy the latched byte. Rejected sprites cost two dots; selected sprites take eight. Once eight are selected, the overflow search advances both address components without carry and can mistake a tile, attribute or X byte for a Y coordinate. The flag is set on the comparison dot, including the early dot-130 and late dot-240 cases.

Fetch dots 257–320 transfer Y, tile, attributes and X from secondary OAM into each output slot. OAMDATA reads expose the clear/evaluation/fetch latch. No evaluation runs on pre-render, and no sprites are enabled for the first visible line; pre-render fetches still use the retained secondary data. Output shifters and X counters continue while either rendering layer is enabled, so hiding a layer does not freeze its pipeline.

The old sprite ROMs predate the `$6000` signature protocol. Their included validation/runtime sources use `$00F8=1` for success and eventually enter a `JMP` to itself. The runner requires both conditions; it does not infer success from a screenshot or from an intermediate result byte. Failure codes and ROM hashes remain visible in the separate report.

Offline coverage includes false-positive and false-negative overflow, wrapping the primary address, read/write latch boundaries, secondary clearing, independent current-line pixels, hidden-layer pipeline clocks, and saves across clear, copy, overflow and fetch dots. OAMADDR corruption, rendering-time writes to OAM, fine details of overflow-tail readback and rendering-toggle corruption remain separate targets; the current passing sprite ROMs do not establish those behaviors.

References: [PPU sprite evaluation](https://www.nesdev.org/wiki/PPU_sprite_evaluation), [hardware overflow test sources](https://github.com/christopherpow/nes-test-roms/tree/95d8f621ae55cee0d09b91519a8989ae0e64753b/sprite_overflow_tests/source), and [sprite-zero-hit tests](https://github.com/christopherpow/nes-test-roms/tree/95d8f621ae55cee0d09b91519a8989ae0e64753b/sprite_hit_tests_2005.10.05).

## DMC stop propagation and aborted DMA

The ninth milestone adds a separate, pinned AccuracyCoin subset: **3/3** selected tests pass, up from **1/3** before this change. The original OAM overlap case remains passing; explicit and implicit DMA abort now match the ROM's timing tables. These results are separate from the complete 55-ROM baseline and from the older 2-ROM DMA suite.

Clearing `$4015` D4 schedules the reader's stop for the PUT phase of the following APU cycle. A second write does not postpone that deadline. A reload which halts at the stop boundary consumes one CPU read cycle; if a write prevents that halt, the request disappears. A transfer already past its halt finishes its bus access, but the disabled reader discards the byte. Buffered audio continues playing. Reset cancels requests immediately.

One-byte non-looping samples can trigger the same aborted reload as their last fetch ends. On the selected late RP2A03G/H model, a fetch overlapping the output-reload APU cycle instead transfers the byte to the shifter and requests the same address again. The equivalent looping case also permits consecutive DMAs. The output-boundary latch is independent of the programmable rate, so changing `$4010` cannot manufacture a reload edge. Earlier RP2A03G behavior is not currently a selectable profile.

An aborted controller read preserves the continuous read-enable signal through the resumed CPU read; it does not shift a second button out. Offline traces cover both ports, read/write cancellation, ordinary and duplicate sample fetches, IRQ status, repeated stop writes, reset and save replay. Other DMA/internal-register bus conflicts remain outside this milestone.

`--dma-abort-suite` verifies the pinned ROM hash and navigates the unmodified menu using controller input. It reads results only from the ROM's own result slots after launching each test; neither results nor prerequisite state are patched. The report preserves raw result codes and all three implicit-stop timing arrays. Success `$05` identifies the late-chip behavior. This is not a score for the full AccuracyCoin collection.

References: [NESdev DMA bugs and cycle diagrams](https://www.nesdev.org/wiki/DMA#Bugs), [AccuracyCoin test source](https://github.com/100thCoin/AccuracyCoin/blob/9bc42d1e3acbeeaea215b1011d58f4ce72a8a49e/AccuracyCoin.asm), and [measured results](dma-stop-results.md). Mesen's DMC reader and CPU DMA implementation were also consulted to cross-check stop propagation and buffer handling; the implementation here retains this core's existing bus-cycle scheduler.

## Selectable $AB opcode profile

The tenth milestone makes the unstable immediate `LAX` ($AB) a configuration choice rather than a fixed answer, and with it the public suite reaches **55/55**. Nothing else changed: the same run still records **6/6 MMC3** with explicit IRQ profiles, and the separate DMA and sprite suites remain **2/2** and **16/16**.

$AB has no single correct result. The opcode drives the internal data bus against a decaying value, so the effective mask depends on the chip, its temperature and the preceding bus activity. Two models are widely reported. `A = X = operand & (A | $EE)` is what the SingleStepTests NES vectors record; `A = X = operand` is what blargg's `03-immediate` expects. Both are selectable here as `AbOpcodeProfile.MaskEE` (the default) and `AbOpcodeProfile.MaskFF`.

The trade-off is measured rather than asserted. With `$EE`, `03-immediate` fails and all 2,560,000 vectors pass. With `$FF`, all 55 ROMs pass and 4,422 of the 10,000 `ab` vectors fail; no other opcode file changes. Each report names the profile it ran under, so no measurement silently mixes the two: the ROM table is a `$FF` run, the vector report an `$EE` run.

The profile is chosen on the command line (`--ab-profile ee|ff`) or from **Emulation → $AB opcode profile**, exactly as the MMC3 revision is. The core never inspects a test name. `$8B` (XAA) keeps its own `$EE` mask and is unaffected, since its result also depends on X.

References: [CPU unofficial opcodes](https://www.nesdev.org/wiki/CPU_unofficial_opcodes) and [programming with unofficial opcodes](https://www.nesdev.org/wiki/Programming_with_unofficial_opcodes) on the unstable constant, the [SingleStepTests NES vectors](https://github.com/SingleStepTests/65x02/tree/2f6980a2d95757486c7bee24355c360e40e2a224/nes6502), and blargg's [`03-immediate` source](https://github.com/christopherpow/nes-test-roms/tree/95d8f621ae55cee0d09b91519a8989ae0e64753b/instr_test-v5/source).

## The complete AccuracyCoin collection

The eleventh milestone replaces the three hand-picked AccuracyCoin tests with the whole collection: **112 of 144** judged tests pass, none time out. Five further entries on the Power On State page print information instead of judging the console; the ROM leaves them out of its own tally and so does this one.

The runner takes nothing on faith from this repository. It verifies the pinned ROM hash, then walks the cartridge's own menu tables — a pointer list at `$8100`, each page holding a title and `name, $FF, result address, routine address` entries — so the page list, the test order and every result slot come from the ROM image. Each test starts from its own power-on, is selected with controller input, and is judged only by the byte the ROM writes into its result slot. Nothing is patched, and no test name is special-cased.

The 32 failures are not scattered: they cluster onto the gaps already on the work list.

| Cluster | Failing tests |
|---|---|
| Open bus, on the cartridge and in the PPU | Open Bus, PPU Register Open Bus, Internal Data Bus, APU Register Activation |
| Controller port timing | Controller Strobing, Controller Clocking |
| `$2004`/OAM behavior during rendering | Address `$2004` behavior, `$2004` Stress Test, Misaligned OAM behavior, OAM Corruption, Arbitrary Sprite zero, `$2002` flag timing |
| `$2007` during rendering | `$2007` read w/ rendering, `$2007` Stress Test |
| Background and sprite shift registers | Stale BG Shift Registers, BG Serial In, ALE + Read, Hybrid Addresses, Sprites On Scanline 0, Stale Sprite Shift Regs, Frozen OAM2 Increment, Misaligned OAM2 Address |
| Unstable store opcodes | SHA, SHS, SHY and SHX (`$93`, `$9F`, `$9B`, `$9C`, `$9E`) |
| Remaining timing edges | Interrupt flag latency, Implied Dummy Reads, Frame Counter IRQ, DMC DMA Bus Conflicts, Palette RAM Quirks |

The public blargg suites stay at 55/55, so this is added coverage rather than a regression: these are behaviors the older ROMs never exercised. The full table, with each ROM error code, is in [accuracy-coin-results.md](accuracy-coin-results.md).

References: [AccuracyCoin](https://github.com/100thCoin/AccuracyCoin/tree/9bc42d1e3acbeeaea215b1011d58f4ce72a8a49e) and its [test source](https://github.com/100thCoin/AccuracyCoin/blob/9bc42d1e3acbeeaea215b1011d58f4ce72a8a49e/AccuracyCoin.asm), which documents what each test expects and why.

## Open bus and the controller ports

The twelfth milestone models the data bus itself instead of returning zero for everything nothing answers. AccuracyCoin goes from **112/144 to 115/144**: `Open Bus`, `Controller Strobing` and `Implied Dummy Reads` now pass, and the blargg suites are unchanged at 55/55, 16/16 and 2/2.

A cartridge only drives the data lines for the addresses it decodes. Every board now says which those are, so a read of `$4020-$5FFF` on a board without work RAM leaves the bus floating and the processor reads back the last value it carried. That is usually the high byte of the address operand, which is why `LDA $5501` returns `$55`; an indexed read that crosses a page returns the *unfixed* high byte, because the extra read is open bus too and changes nothing.

`$4015` is answered inside the 2A03 and never reaches the external bus. Reading it therefore leaves the bus alone — a following open-bus read still sees the older value — and its unused bit reads back whatever the bus was carrying. Writes always drive the bus, `$4015` included.

The controller ports drive only their data lines. The three bits above them come from the bus, so `LDA $4016` reports `$40` in them while a dummy read through `$2006` leaves `$E0` there. The old fixed `$40` is gone.

Two port behaviors are timing, not decoding. The parallel load is level triggered on the processor's get-to-put transition, so a one-cycle strobe — the `$41` then `$40` that `DEC $4016` writes — only reaches the shift register on one of the two alignments. And a port is clocked by its output enable rising, which does not happen between two contiguous reads of the same address: a read-modify-write's double read clocks the pad once, and so do the stalled reads of a DMA halt. The DMA-specific special case for that is gone; one rule now covers both.

References: [open bus](https://www.nesdev.org/wiki/Open_bus_behavior), [controller port reads](https://www.nesdev.org/wiki/Standard_controller#Output_.28.244016.2F.244017_read.29), and AccuracyCoin's [`Open Bus`, `Controller Strobing` and `Controller Clocking` sources](https://github.com/100thCoin/AccuracyCoin/blob/9bc42d1e3acbeeaea215b1011d58f4ce72a8a49e/AccuracyCoin.asm).

Still missing: the PPU's own open bus and its decay over time, and the DMC DMA's bus conflicts with the 2A03 registers.

## Transfer bus conflicts with the 2A03 registers

The thirteenth milestone models what the sound chip's own registers do while a transfer holds the processor. AccuracyCoin goes from **115/144 to 118/144**: `DMC DMA Bus Conflicts`, `APU Register Activation` and `Controller Clocking` now pass, with the blargg suites unchanged.

Only five address lines reach the register decoder inside the 2A03, so `$4015`, `$4016` and `$4017` are mirrored every `$20` bytes across the whole address space. What keeps a game from tripping over that is a second condition: the registers answer only while the *processor's* address is inside `$4000-$401F`. A transfer changes the address on the pins but not the one the stalled processor is presenting, and the two conditions then come apart. A sample fetch from `$FF16` reads controller 1 at the same time, and a sprite transfer through a page of open bus collects the status byte at every `$x15` — while the same transfer with the processor stalled anywhere else reads nothing at all.

When two drivers meet on the same lines, which one is visible depends on what they are. The status register drives every line but its unused bit, and wins over work RAM; that bit comes from whatever else is on the bus. A controller's data lines win over a cartridge, so a sample fetch shows the pad's bits under the sample's top three — but lose to work RAM, so a sprite transfer out of RAM records the RAM byte and the pads stay invisible even though they are still being clocked. Both of those are what the ROM's own answer keys record.

Reading a mirrored `$4015` still acknowledges the frame interrupt, which is how the ROM detects the whole effect in the first place, and it still leaves the external bus alone.

One sprite-memory detail came out of the same answer keys: three bits of each sprite's attribute byte have no storage behind them, so they are dropped on the way into OAM and read back as zero.

References: AccuracyCoin's [`DMC DMA Bus Conflicts` and `APU Register Activation` sources](https://github.com/100thCoin/AccuracyCoin/blob/9bc42d1e3acbeeaea215b1011d58f4ce72a8a49e/AccuracyCoin.asm), whose comments carry the expected sprite-memory contents byte by byte, and [NESdev on DMA bugs](https://www.nesdev.org/wiki/DMA#Bugs).

## Validation and save compatibility

- 717 offline checks, including CPU/PPU/APU timing, DMA arbitration/stop windows, MMC3 revisions/M2 filtering, sprite evaluation, both `$AB` profiles and real v2–v9 state migration fixtures.
- The complete baseline ROM report records the selected IRQ profiles and the `$AB` profile it ran under; DMA results are reported separately.
- The complete AccuracyCoin collection is measured test by test, with every failure and error code listed.
- 95 desktop input and menu checks.
- Offline bus checks cover unmapped reads, `$4015`'s internal path, the port's undriven bits, the strobe alignment, contiguous port reads and the transfer conflicts against open bus, work RAM and a stall outside the register range.
- The independent CPU vector suite checks registers, memory and every bus operation for all 256 opcodes.
- Local Mega Man 4 and Super Mario Bros. 3 runs exercise game input, rendering and audio; their ROMs and generated captures remain outside version control.

New saves use format v11, adding the controller port's output-enable address and the value it is presenting. Format v10 added one header byte for the `$AB` profile. Existing v2–v9 saves remain loadable and are treated as `$EE`, the profile they were written under. Like the MMC3 revision, the profile is checked before any console state is touched: a state from the other profile is refused with a message naming it, because the two models produce different emulated results. A real v9 snapshot written by the previous serializer verifies both paths: it migrates into the `$EE` profile, replays identically for 300 further instructions, and is refused under `$FF`. Format v9 added five bytes for the pending DMC stop and output-reload age, and v2–v8 saves still start without a pending stop or recent output-boundary latch.

v8 added 41 bytes for secondary OAM and the evaluation/fetch latches. Earlier formats have no partial sprite search: migration reconstructs it from saved primary OAM during evaluation, or retains the selected sprite data during fetch/blanking, preserving existing output shifters and status flags. Earlier OAM writes within that line cannot be reconstructed; v8 captures the actual partial state. Real v7 snapshots at dots 100 and 270 verify both migration paths.

v7 added one filter-progress byte to MMC3 payloads. v6 introduced the IRQ-revision byte in the header. A mismatched profile is rejected before changing live state; v2–v5 imply standard MMC3, while later formats preserve the selected profile. For pre-v7 MMC3 states, filter progress is reconstructed from the saved A12-low timestamp and elapsed PPU clock at the instruction boundary, where M2 has just fallen; both v6 profiles have real migration fixtures. v5 introduced the DMC buffer, pending DMA delay and GET/PUT phase, after the v4 APU reset delay and v3 CPU/PPU timing latches. Pre-v5 DMC states retain their output shifter and unread sample address, start with an empty prefetch buffer, and schedule a fetch if the reader is active. v2/v3 states have no pending APU reset. For v2, migration also seeds the CPU's sampled NMI level and transfers any pending PPU NMI event. The application version remains 1.0.0.

```powershell
dotnet run -c Release --project tests/NesEmulator.Tests
dotnet run -c Release --project tests/NesEmulator.Tests -- --rom-suite roms/accuracy docs/accuracy-results.md
dotnet run -c Release --project tests/NesEmulator.Tests -- --dma-suite roms/accuracy docs/dma-results.md
dotnet run -c Release --project tests/NesEmulator.Tests -- --sprite-suite roms/accuracy docs/sprite-results.md
dotnet run -c Release --project tests/NesEmulator.Tests -- --dma-abort-suite roms/accuracy-coin docs/dma-stop-results.md
dotnet run -c Release --project tests/NesEmulator.Tests -- --cpu-vectors roms/cpu-vectors
```

## Next targets

1. Remaining DMA quirks: hybrid `$4000–$401F` register selection during DMA and adjacent PPUDATA-read behavior. These are not established by the current passing suites.
2. OAMADDR/write corruption, rendering-time OAM accesses, and PPUMASK transition behavior, with additional public suites.

Each milestone should retain the previous passing checks and report its remaining mismatches. The additional DMA and sprite suites extend coverage beyond the original 55-ROM set; they do not prove every PPU/bus interaction.
