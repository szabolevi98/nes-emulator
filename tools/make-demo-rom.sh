#!/usr/bin/env bash
# Builds roms/demo.nes: the smallest cartridge that gives the trace window
# something to chew on. Mapper 0, 16 KB of program ROM, 8 KB of character RAM.
#
# The program itself sits at $C000 and loops forever:
#
#   C000  A9 00     LDA #$00
#   C002  A2 00     LDX #$00
#   C004  E8        INX
#   C005  18        CLC
#   C006  69 03     ADC #$03
#   C008  4C 04 C0  JMP $C004
#
# Run it from the repository root: bash tools/make-demo-rom.sh
set -euo pipefail

out="roms/demo.nes"
mkdir -p roms

{
    # iNES header: signature, one 16 KB program bank, no character banks.
    printf 'NES\x1a\x01\x00\x00\x00'
    printf '\x00\x00\x00\x00\x00\x00\x00\x00'

    # 16 KB of program ROM, zero filled.
    head -c 16384 /dev/zero
} > "$out"

write_bytes() { # offset, then bytes as escape sequences
    local offset=$1
    shift
    printf "$@" | dd of="$out" bs=1 seek=$((16 + offset)) conv=notrunc status=none
}

write_bytes 0x0000 '\xa9\x00\xa2\x00\xe8\x18\x69\x03\x4c\x04\xc0'
write_bytes 0x3ffc '\x00\xc0'   # reset vector -> $C000
write_bytes 0x3ffa '\x00\xc0'   # non-maskable interrupt vector
write_bytes 0x3ffe '\x00\xc0'   # interrupt request vector

echo "wrote $out ($(wc -c < "$out") bytes)"
