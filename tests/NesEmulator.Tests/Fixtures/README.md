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
