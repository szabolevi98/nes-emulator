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
- **Decimal mode is fused off.** The 2A03 is a 6502 with binary coded decimal disabled, so `SED` sets a flag that changes nothing about how `ADC` adds.

Each of those has a test that fails if the shortcut is taken instead.

`Cpu/OpcodeTable.cs` holds all 256 entries written out in full, four to a line, so a row matches a row of the published opcode matrix and can be checked against it by eye. Writing it as data rather than as a switch is also what makes the disassembler nearly free.

## Following the beam

The picture unit does not draw a frame and hand it over. It produces one pixel per cycle while a television beam sweeps the screen, 341 cycles across and 262 lines down, three of its cycles for every processor cycle. Games use that: they change the scroll position partway down a frame so a status bar stays still while the level moves underneath it, and they find the right moment by watching for the sprite zero hit flag, which goes up the instant an opaque sprite pixel lands on an opaque background pixel.

So the renderer follows the beam rather than walking tiles. Its scroll position lives in two fifteen bit registers, packed as fine y, name table, coarse y, coarse x, and the bit manipulation in `Ppu/Ppu2C02.cs` is that layout being incremented and copied the way the hardware does it — including coarse y counting to 29 rather than 31, because the last two rows of a name table hold attribute bytes rather than tiles.

Two kilobytes of name table memory has to cover four screens, so the cartridge wires the same memory into two of the four slots. Which two is what mirroring means, and it is why the demo cartridge scrolls sideways forever across a single screen of tiles.

## Why the sound is not a sum

Five channels — two square waves, a triangle, a noise generator and a sample player — feed a resistor ladder rather than an adder. The result is not linear: a loud channel compresses the others, so the same note is quieter in a busy passage than in a bare one. Adding the channels together instead is the usual reason an emulator sounds harsh and thin, so the mixer here uses the published approximations of that ladder.

The rest of the chip divides cleanly in two. The channel timers run off the processor clock and produce the waveform; a separate frame counter ticks four or five times a frame and clocks the parts that shape a note over time — the volume envelopes, the pitch sweeps and the length counters that keep a sound playing after the game has moved on. That counter is also the only interrupt a game can get without a cartridge that provides its own.

Two details carry more of the console's character than their size suggests. The triangle steps through a fixed thirty-two step staircase with no volume control at all, which is where that hollow bass tone comes from. And the noise channel is a shift register feeding back on itself; flipping one bit changes which bit it taps, shortening the cycle from 32,767 steps to 93 — short enough to hear as a pitch, which is how the same channel gives both hissing static and metallic engine sounds.

Playback goes out through the Windows wave API, and the number of buffers the sound card has finished with is what paces the emulator. Timing the frames off a clock instead would drift against the card and break the audio up. The signal is band limited before it is decimated to the output rate; [audio implementation and validation](audio-quality.md) covers that path and how it is checked.

## Winding back

A save state is everything that can change while a game runs — work RAM, the picture unit's memory and registers, the sound unit's counters, the cartridge's own RAM and bank registers, and the finished picture so that loading mid-frame does not show half of the old one. The cartridge ROM is not in it, which is what keeps a state to about seventy kilobytes.

That is still too much to keep once a frame, so rewind takes a snapshot every tenth frame and deflates it. A console's memory is mostly repeated bytes and long runs of zero, so they come down to around five kilobytes each: three hundred and sixty of them, a minute of play, costs under two megabytes. Holding backspace walks back through them.

State format v3 carries the mapper number, a SHA-256 identity of the original ROM image, the payload size and its SHA-256 checksum. A different ROM is refused even if it uses the same mapper. Truncated or corrupted payloads are rejected before any live console state changes. It stores the CPU's sampled NMI input and the PPU's suppression and render-enable latches as well as the CPU interrupt samples and mapper address-edge state. Existing v2 saves are migrated when loaded, including an unconsumed PPU NMI event; v1 remains unsupported.

## Running it from a debugger

The core is built optimised in every configuration, including Debug. It is a real-time simulation: 29,781 processor cycles and 733 band-limited audio samples have to be finished inside each 16.6 ms frame, and an unoptimised build manages roughly half of that. The emulator then runs at half speed and the sound card is left with nothing to play, which sounds like broken audio rather than like a slow machine. The app and test projects are still built unoptimised, so breakpoints and stepping in the user interface behave normally.

The debugger panel reports an audio gap count beside the frame rate. Any number above zero after startup means the emulator is not feeding the sound card fast enough, which is a performance problem rather than a fault in the sound unit.
