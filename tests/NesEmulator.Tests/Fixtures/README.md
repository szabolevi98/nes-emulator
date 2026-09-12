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
