# Save-state migration fixtures

`v2-held-nmi.state.gz` and `v2-pending-nmi.state.gz` are real format-v2 snapshots
written by the core from commit `6e02713`, before the CPU/PPU NMI timing change.
They contain only synthetic test data, no commercial cartridge contents.

The cartridge is `BuildNmiTimingRom()` in the test runner: `LDA $2002`, an idle
loop at `$C003`, and an NMI routine at `$C020` that increments `$10` then returns.
Creation jumps the CPU to `$C003`, advances the PPU to scanline 241, dot 5,
enables NMI, and runs 20 instructions. The first snapshot has already serviced
one interrupt while the PPU's NMI output remains asserted. The second toggles
PPUCTRL's NMI enable off and on without running the CPU, leaving a pending PPU
event in the old format. Both snapshots are gzip-compressed without modifying
their bytes.

These fixtures ensure migration is checked against bytes produced by the old
serializer, including held-line and unconsumed-event behavior.

`v3-held-nmi.state.gz` was written by the core from commit `7f989e2` using the
same held-line procedure. It checks that the CPU/PPU timing latches survive
migration while the newly added APU reset delay defaults to zero.

`v4-active-dmc.state.gz` was written by the core from commit `24b552a` using
the same held-NMI procedure, then writing `$4010=$0F`, `$4012=0`, `$4013=1`
and `$4015=$10` without executing another CPU cycle. It checks migration of an
active DMC reader before its first fetch, including the original sample address.

`v5-mmc3-dmc.state.gz` was written by the core from commit `f468b9b` using
`Mmc3RevisionTests.Image()`: 32 KB PRG, 8 KB CHR, mapper 4, and `JMP $E000`
with the reset vector pointing there. After 300 instructions (907 CPU cycles),
creation writes `$6000=$A5`, sets the IRQ latch to 2, requests reload, enables
IRQ, and drives A12 low/high at dots 1/20 and 21/40. The counter is now 1.
It then writes `$4010=$0F`, `$4012=0`, `$4013=1`, `$4015=$10` without another
CPU cycle and saves. This checks the unchanged v5 payload, pending DMC fetch,
mapper counter, and the standard IRQ profile implied by older headers.

`v6-mmc3-standard-low.state.gz` and `v6-mmc3-alternate-low.state.gz` were
written by the core from commit `c239b8a`, using the same synthetic cartridge
and the named IRQ profile. After the seven reset cycles, creation sets PPUADDR
to `$1000`, writes `$C001=0` and `$E001=0`, and supplies the first PPUADDR byte
for `$0000`. A real `STA $2006` instruction at `$0200`, with A=0, supplies the
second byte. At the save boundary PC is `$0203`, CPU cycles are 11, the PPU
clock is 33, and A12 has been low since dot 32. Exactly one M2 falling edge
has occurred in that interval. These fixtures check filter migration for both
profiles against the old serializer, including v6's IRQ-profile header.

`v7-sprites-dot-100.state.gz` and `v7-sprites-dot-270.state.gz` were written
by the core from commit `45c3067` using the synthetic cartridge/setup in
`SpriteEvaluationTests.Machine()`: NROM with CHR RAM, a `JMP $8000` program,
a solid tile, and nine sprites at Y=10 and X=0,8,...,64. After setup, only
the PPU advances to scanline 10, next dot 100 or 270. The first snapshot
precedes the old batched evaluation; the second contains its selected sprites
partway through pattern fetching. Both verify that v8 migration preserves
the next line's eight visible sprites. They contain no commercial ROM data.

`v8-dmc-0.state.gz` and `v8-dmc-20.state.gz` were written by the core from
commit `67557ea`. They use `DmaTests.NewNes()` (32 KB of synthetic $55 PRG,
8 KB of zero CHR), with `JMP $0200` in RAM, `$4010=$4F`, and `$4015=$10`.
The snapshots follow zero or twenty instructions: the first has a pending
load request, the second has a filled sample buffer. v9 migration must add
no pending stop or recent output-reload edge and must preserve subsequent
execution. These fixtures contain no commercial ROM data.
