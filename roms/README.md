# ROMs

`demo.nes` is a handwritten cartridge built by `tools/make-demo-rom.sh`. It is
eleven bytes of program in a mapper 0 shell, looping forever so that the trace
window has something to show.

Everything else in this folder is ignored by Git. No commercial ROMs belong
here: they are copyrighted, and the emulator does not need them to be tested.

As the hardware emulation grows, this is where the freely distributed accuracy
test ROMs go — the ones that check instruction behaviour, timing and picture
unit edge cases and report a pass or fail of their own.
