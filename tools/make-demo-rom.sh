#!/usr/bin/env bash
# Builds roms/demo.nes, a handwritten cartridge that exercises the parts of the
# picture unit worth seeing: a palette, a screen full of tiles, and a scroll that
# moves every frame because the interrupt handler rewrites it.
#
# Mapper 0, 16 KB of program ROM mapped at $C000, 8 KB of tile ROM.
# Run it from the repository root: bash tools/make-demo-rom.sh
#
#   ---- reset ----------------------------------------------------------------
#   C000  78         SEI
#   C001  D8         CLD
#   C002  A2 FF      LDX #$FF
#   C004  9A         TXS
#   C005  A9 00      LDA #$00
#   C007  8D 00 20   STA $2000          ; interrupts off
#   C00A  8D 01 20   STA $2001          ; rendering off
#   C00D  2C 02 20   BIT $2002          ; the picture unit needs two frames
#   C010  10 FB      BPL $C00D          ; to warm up before it accepts writes
#   C012  2C 02 20   BIT $2002
#   C015  10 FB      BPL $C012
#   ---- palette --------------------------------------------------------------
#   C017  A9 3F      LDA #$3F
#   C019  8D 06 20   STA $2006          ; address $3F00
#   C01C  A9 00      LDA #$00
#   C01E  8D 06 20   STA $2006
#   C021  A2 00      LDX #$00
#   C023  BD A0 C0   LDA $C0A0,X        ; copy 32 bytes from the table below
#   C026  8D 07 20   STA $2007
#   C029  E8         INX
#   C02A  E0 20      CPX #$20
#   C02C  D0 F5      BNE $C023
#   ---- name table -----------------------------------------------------------
#   C02E  A9 20      LDA #$20
#   C030  8D 06 20   STA $2006          ; address $2000
#   C033  A9 00      LDA #$00
#   C035  8D 06 20   STA $2006
#   C038  A0 04      LDY #$04           ; four passes of 256 bytes covers the
#   C03A  A2 00      LDX #$00           ; 960 tiles and the attributes after them
#   C03C  8A         TXA
#   C03D  4A         LSR A              ; tile number changes every eight squares
#   C03E  4A         LSR A
#   C03F  4A         LSR A
#   C040  29 07      AND #$07
#   C042  18         CLC
#   C043  69 01      ADC #$01           ; tiles 1 to 8
#   C045  8D 07 20   STA $2007
#   C048  E8         INX
#   C049  D0 F1      BNE $C03C
#   C04B  88         DEY
#   C04C  D0 EC      BNE $C03A
#   ---- go -------------------------------------------------------------------
#   C04E  A9 00      LDA #$00
#   C050  8D 05 20   STA $2005          ; scroll to the origin
#   C053  8D 05 20   STA $2005
#   C056  A9 1E      LDA #$1E           ; show everything, including the edges
#   C058  8D 01 20   STA $2001
#   C05B  A9 80      LDA #$80
#   C05D  8D 00 20   STA $2000          ; ask for an interrupt each frame
#   C060  4C 60 C0   JMP $C060          ; the handler does the rest
#   ---- interrupt ------------------------------------------------------------
#   C070  48         PHA
#   C071  AD 02 20   LDA $2002          ; clears the flag and the write latch
#   C074  EE 00 00   INC $0000
#   C077  AD 00 00   LDA $0000
#   C07A  8D 05 20   STA $2005          ; scroll one pixel further across
#   C07D  A9 00      LDA #$00
#   C07F  8D 05 20   STA $2005
#   C082  68         PLA
#   C083  40         RTI
#   C090  40         RTI                ; for the maskable interrupt vector
set -euo pipefail

out="roms/demo.nes"
mkdir -p roms

# 16 byte header, one 16 KB program bank, one 8 KB tile bank, horizontal
# mirroring so that scrolling sideways wraps around the one screen we fill.
printf 'NES\x1a\x01\x01\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00' > "$out"
head -c $((16384 + 8192)) /dev/zero >> "$out"

prg=16
chr=$((16 + 16384))

put() { # absolute offset, then bytes as hex
    echo "$2" | xxd -r -p | dd of="$out" bs=1 seek="$1" conv=notrunc status=none
}

put $((prg + 0x0000)) "\
78 d8 a2 ff 9a a9 00 8d 00 20 8d 01 20 2c 02 20 \
10 fb 2c 02 20 10 fb a9 3f 8d 06 20 a9 00 8d 06 \
20 a2 00 bd a0 c0 8d 07 20 e8 e0 20 d0 f5 a9 20 \
8d 06 20 a9 00 8d 06 20 a0 04 a2 00 8a 4a 4a 4a \
29 07 18 69 01 8d 07 20 e8 d0 f1 88 d0 ec a9 00 \
8d 05 20 8d 05 20 a9 1e 8d 01 20 a9 80 8d 00 20 \
4c 60 c0"

put $((prg + 0x0070)) "48 ad 02 20 ee 00 00 ad 00 00 8d 05 20 a9 00 8d 05 20 68 40"
put $((prg + 0x0090)) "40"

# Four background palettes, then the same four for sprites. Entry zero of each is
# the shared backdrop.
put $((prg + 0x00A0)) "\
0f 16 27 18 0f 11 21 31 0f 19 29 39 0f 14 24 34 \
0f 16 27 18 0f 11 21 31 0f 19 29 39 0f 14 24 34"

put $((prg + 0x3FFA)) "70 c0"   # interrupt
put $((prg + 0x3FFC)) "00 c0"   # reset
put $((prg + 0x3FFE)) "90 c0"   # maskable interrupt

# Tiles 1 to 8: a six by six block inset in each eight by eight square, so the
# screen reads as a grid. Each tile uses one of the three colours of its palette,
# which is two bit planes: the low plane carries bit 0, the high plane bit 1.
for tile in 1 2 3 4 5 6 7 8; do
    colour=$(( ((tile - 1) % 3) + 1 ))
    low=""
    high=""
    for row in 0 1 2 3 4 5 6 7; do
        if [ "$row" -eq 0 ] || [ "$row" -eq 7 ]; then
            low="$low 00"
            high="$high 00"
        else
            [ $((colour & 1)) -eq 1 ] && low="$low 7e" || low="$low 00"
            [ $((colour & 2)) -eq 2 ] && high="$high 7e" || high="$high 00"
        fi
    done
    put $((chr + (tile * 16))) "$low $high"
done

echo "wrote $out ($(wc -c < "$out") bytes)"
