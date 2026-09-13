# How it works

Notes on the parts of the console that are interesting to implement, and on the
places where getting it right means reproducing something the hardware does
badly. The [README](../README.md) covers what the emulator is and how to run it.

## The parts that are easy to get wrong

Writing a processor is mostly transcription. The interesting work is in the handful of places where the real chip does something a clean implementation would not, and where games came to rely on exactly that:

- **The indirect jump bug.** `JMP ($30FF)` reads its high byte from `$3000`, not `$3100`, because the increment never carries into the high half of the address. Shipped games depend on this, so it is part of the contract rather than a defect to fix.
- **Page crossing costs a cycle, but only for reads.** `LDA $80FF,X` takes a fifth cycle when the index pushes it into the next page, because the processor has to correct the address it already guessed. A store takes the long path every time, so its cost never changes.
- **Read-modify-write writes twice.** `INC $20` puts the unmodified value back before it writes the new one. On work RAM nothing notices, but some hardware registers react to the first write, and a version that optimises it away behaves differently on real games.
- **The break flag is not a real flag.** Nothing stores it in the status register; it only exists in the copy pushed onto the stack, and its value says whether the push came from an instruction or from an interrupt.
- **Interrupt polls have exceptions.** A taken branch within a page keeps its early poll, delaying a newly arriving IRQ or NMI until after the next instruction. An NMI can redirect a BRK/IRQ already entering its handler, but only before vector selection; a later NMI waits for the first handler instruction. The original stack frame survives a redirected vector.
- **Decimal mode is fused off.** The 2A03 is a 6502 with binary coded decimal disabled, so `SED` sets a flag that changes nothing about how `ADC` adds.

Each of those has a test that fails if the shortcut is taken instead.

`Cpu/OpcodeTable.cs` holds all 256 entries written out in full, four to a line, so a row matches a row of the published opcode matrix and can be checked against it by eye. Writing it as data rather than as a switch is also what makes the disassembler nearly free.

## Following the beam

The picture unit does not draw a frame and hand it over. It produces one pixel per cycle while a television beam sweeps the screen, 341 cycles across and 262 lines down, three of its cycles for every processor cycle. Games use that: they change the scroll position partway down a frame so a status bar stays still while the level moves underneath it, and they find the right moment by watching for the sprite zero hit flag, which goes up the instant an opaque sprite pixel lands on an opaque background pixel.

So the renderer follows the beam rather than walking tiles. Its scroll position lives in two fifteen bit registers, packed as fine y, name table, coarse y, coarse x, and the bit manipulation in `Ppu/Ppu2C02.cs` is that layout being incremented and copied the way the hardware does it — including coarse y counting to 29 rather than 31, because the last two rows of a name table hold attribute bytes rather than tiles.

Two kilobytes of name table memory has to cover four screens, so the cartridge wires the same memory into two of the four slots. Which two is what mirroring means, and it is why the demo cartridge scrolls sideways forever across a single screen of tiles.

Cartridges also see PPU addresses that never become completed reads. The aborted pattern fetch at the end of a line changes A12 and affects MMC3 interrupt counting. It is sent to the mapper's address observer without reading CHR data; the odd pre-render skip omits it along with the skipped dot.

## Colour that is not a palette entry

The three high bits of the mask register are the one part of the picture that no
palette table can express. They do not brighten a channel; they hold the other
two back, because the signal spends longer at the emphasised phase and the rest
come out dimmer. Setting all three darkens the picture rather than lighting it.

Games use this for a screen-wide flash, for tinting everything while under
water, and for the frame a hit lands on. Because it can change partway down a
frame, the setting belongs to the pixel rather than to the frame: each entry in
the finished picture carries its palette index in the low six bits and the
emphasis in force when the beam passed in the three above it.

## Why the sound is not a sum

Five channels — two square waves, a triangle, a noise generator and a sample player — feed a resistor ladder rather than an adder. The result is not linear: a loud channel compresses the others, so the same note is quieter in a busy passage than in a bare one. Adding the channels together instead is the usual reason an emulator sounds harsh and thin, so the mixer here uses the published approximations of that ladder.

The rest of the chip divides cleanly in two. The channel timers run off the processor clock and produce the waveform; a separate frame counter ticks four or five times a frame and clocks the parts that shape a note over time — the volume envelopes, the pitch sweeps and the length counters that keep a sound playing after the game has moved on. That counter is also the only interrupt a game can get without a cartridge that provides its own.

The DMC reads a byte ahead into a buffer, independently of the eight-bit shifter producing sound. Its reader halts the CPU at an eligible read cycle and shares the bus with OAM DMA: DMC fetches take priority, while halt and alignment cycles can overlap sprite transfers. Finishing the memory reader can raise an IRQ before the last buffered sound has played. Disabling the reader leaves those buffered bits intact.

Stopping the reader takes a short phase-dependent delay. A reload can halt the CPU for one cycle before that stop arrives, then abort; a write on the same cycle prevents the halt altogether. A one-byte sample ending at the output-reload boundary can also fetch the same byte twice on late RP2A03G/H CPUs. The emulator reproduces both cases, checked against AccuracyCoin's explicit and implicit abort tests.

Two details carry more of the console's character than their size suggests. The triangle steps through a fixed thirty-two step staircase with no volume control at all, which is where that hollow bass tone comes from. And the noise channel is a shift register feeding back on itself; flipping one bit changes which bit it taps, shortening the cycle from 32,767 steps to 93 — short enough to hear as a pitch, which is how the same channel gives both hissing static and metallic engine sounds.

Playback goes out through the Windows wave API, and the number of buffers the sound card has finished with is what paces the emulator. Timing the frames off a clock instead would drift against the card and break the audio up. The signal is band limited before it is decimated to the output rate; [audio implementation and validation](audio-quality.md) covers that path and how it is checked.

## A chip that is not listening yet

A reset does not make the picture unit usable straight away. For about a frame
afterwards it ignores the four registers that steer it — control, mask, scroll
and address — while sprite memory and the data port answer normally throughout.
This is why games written for the hardware wait for two vertical blanks before
touching anything: they are waiting for the chip to start listening.

Power-on is modelled as a settled chip rather than a warming one, so a freshly
constructed console accepts those registers immediately. That keeps test rigs
short; a reset goes through the real wait.

## One data line, and the writes that do not count

The MMC1 has a single data line, so a game cannot hand it a bank number: it
writes five times, one bit at a time, and the chip clocks them into a shift
register. That makes the board unusually sensitive to how a write is produced.

A read-modify-write instruction puts the unchanged value back before the new
one, so `INC $8000` performs two writes on consecutive cycles. The chip ignores
any write landing on the cycle after another one, taking a run of them as the
first alone — without that filter the second write clocks a stray bit in and
the game ends up on a bank it never asked for.

## A board with no fixed half

Most boards keep part of the window still so the processor always has ground to
stand on. The AxROM boards behind Battletoads and Marble Madness keep none of
it: one write swaps all thirty-two kilobytes, interrupt vectors included, so the
code doing the swapping has to exist at the same address in every bank or the
next instruction comes from somewhere unintended.

The same write also picks the name table. These boards wire only one, so the
picture is single screen and bit four says which — there is no horizontal or
vertical arrangement to read out of the header at all.

A few variants let the ROM answer a write alongside the latch, so the chip
receives the written value ANDed with the byte already at that address. That is
not something to guess at: NES 2.0 submapper 2 says a cartridge behaves that
way, and anything else is taken as the ordinary case, since ANDing a write a
game did not expect to be ANDed sends it to the wrong bank.

## The blank strip down the left

Start a scrolling game and eight pixels at the left of the screen stay one flat
colour. It looks like a fault and is not: two bits in `$2001` tell the picture
unit to hold the leftmost eight pixels of the background and of the sprites
back, and those pixels then show the backdrop colour, whatever palette entry
zero happens to be.

Games ask for it because the left edge is where the seams are. A scrolling level
is drawn tile by tile, and the column being written as the level comes in can be
caught half updated. Sprites have it worse: their X coordinate has no negative
values, so something entering from the left cannot slide in a pixel at a time —
it would appear all at once. Hiding eight pixels covers both.

On a television of the period nobody saw the strip at all; the bezel took rather
more than that off every edge. It is only visible because a modern window shows
all 256 by 240 pixels honestly. **View → Crop overscan** restores the old view
for anyone who prefers it, and stays off by default: what the console produced
is the thing this emulator is for.

## A cartridge that watches the screen being drawn

Most boards only ever hear from the game. The MMC2 listens to the picture unit
instead: it watches the address of every tile fetch, and when one lands on tile
$FD or $FE it switches the half of the pattern table that fetch came from.

Punch-Out!! is why the chip exists. An opponent takes up half the screen and
needs far more tile memory than the console can address at once, so the tiles
are arranged with a $FD in one row and a $FE in another, and the bank flips
underneath the beam as it crosses the sprite. The game never writes a thing
while this happens.

Two details decide whether it works. The switch takes effect after the fetch
that caused it, so the triggering tile is drawn from the old bank and everything
below it from the new one — do it the other way round and the boxer tears along
a horizontal line. And the addresses watched are narrower than they look: the
MMC2's lower window reacts to $0FD8 and $0FE8 alone, its upper window and both
of the MMC4's to the whole eight-address row. Those are high-plane fetches, so
what the chip is really matching is one specific tile being drawn.

## Winding back

A save state is everything that can change while a game runs — work RAM, the picture unit's memory and registers, the sound unit's counters, the cartridge's own RAM and bank registers, and the finished picture so that loading mid-frame does not show half of the old one. The cartridge ROM is not in it, which is what keeps a state to about seventy kilobytes.

That is still too much to keep once a frame, so rewind takes a snapshot every tenth frame and deflates it. A console's memory is mostly repeated bytes and long runs of zero, so they come down to around five kilobytes each: three hundred and sixty of them, a minute of play, costs under two megabytes. Holding backspace walks back through them.

State format v11 carries the mapper number, the selected MMC3 IRQ profile, the selected `$AB` opcode profile, a SHA-256 identity of the original ROM image, the payload size and its SHA-256 checksum. A different ROM, IRQ profile or `$AB` profile is refused even if it uses the same mapper. Truncated or corrupted payloads are rejected before any live console state changes. Along with the CPU/PPU/APU timing latches, it stores the DMC prefetch buffer, DMA request delay, GET/PUT phase, MMC3 M2 filter progress, the partial sprite evaluation state, and DMC stop/output-reload latches. Existing v2–v10 saves are migrated when loaded with their original IRQ profile (standard for v2–v5) and the `$EE` `$AB` profile they were written under, including an unconsumed PPU NMI event from v2 and an active DMC reader from older formats; v1 remains unsupported.

## Running it from a debugger

The core is built optimised in every configuration, including Debug. It is a real-time simulation: 29,781 processor cycles and 733 band-limited audio samples have to be finished inside each 16.6 ms frame, and an unoptimised build manages roughly half of that. The emulator then runs at half speed and the sound card is left with nothing to play, which sounds like broken audio rather than like a slow machine. The app and test projects are still built unoptimised, so breakpoints and stepping in the user interface behave normally.

The debugger panel reports an audio gap count beside the frame rate. Any number above zero after startup means the emulator is not feeding the sound card fast enough, which is a performance problem rather than a fault in the sound unit.
