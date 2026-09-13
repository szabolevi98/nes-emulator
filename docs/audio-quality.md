# Audio quality

The earlier output sampled the mixer directly about once every 40 CPU cycles. High-frequency waveform harmonics could fold into audible frequencies, and the playback backend only removed a slowly tracked DC offset. The noise period table was also treated as CPU/2 ticks plus one, even though its entries already specified complete CPU-cycle periods. That made percussion slower and lower than intended.

The signal path now is:

1. Clock the channels and mix their DAC levels every CPU cycle.
2. Apply a Blackman-windowed sinc low-pass filter before converting to the requested sample rate. The kernel spans 48 output samples, with 32 fractional CPU-cycle phases and SIMD convolution. This adds about 0.54 ms of filter delay at 44.1 kHz.
3. Apply first-order high-pass filters at 90 and 440 Hz, then a low-pass at 14 kHz, approximating the [NES output circuit](https://www.nesdev.org/wiki/APU_Mixer).
4. Convert signed PCM to 16-bit audio in the Windows backend. The host checks buffer availability before removing samples from the APU queue.

The [noise period table](https://www.nesdev.org/wiki/APU_Noise) is converted to CPU/2 timer units, including the zero tick. DMC timers likewise include their zero tick, fixing the extra CPU cycle per bit. DMC looping now restarts the reader when the last byte is fetched, removing an extra silent byte between repetitions and preventing disabled loops from restarting. The triangle sequencer continues at ultrasonic periods as [the hardware does](https://www.nesdev.org/wiki/APU_Triangle); the resampler removes those frequencies.

## Validation

Run `dotnet run -c Release --project tests/NesEmulator.Tests` for the 451 offline checks. Audio regressions cover all 16 noise periods against a CPU-clocked reference sequence, DMC bit spacing, prefetch, looping and DMA, APU frame-counter reset and IRQ timing, and the following at both 44.1 and 48 kHz:

- A 1 kHz signal remains audible with the expected filter gain.
- A 30 Hz signal is attenuated by the output high-pass filters.
- A signal 1 kHz above output Nyquist is rejected by more than 46 dB relative to the 1 kHz reference, instead of appearing as an audible alias.
- Resetting the filter to a held DAC level does not produce a startup impulse.

All eight public APU ROMs now pass, including `7-dmc_basics` and `8-dmc_rates`. The full baseline suite passes 54/55 with explicit MMC3 IRQ profiles, and both additional OAM/DMC DMA ROMs pass. All previously passing ROMs remain passing. The complete results are in [accuracy-results.md](accuracy-results.md) and [dma-results.md](dma-results.md).

Local runs with the user's Mega Man 4 and Super Mario Bros. 3 images also generated before/after PCM recordings. These ROMs and recordings are excluded from version control. This validates the output path with real game code; it is not a comparison against a physical console recording.

## Remaining limits

The DMC has a separate prefetch buffer and shares CPU bus arbitration with OAM DMA. Remaining hardware cases include stop/abort glitches, hybrid internal-register selection during DMA, and adjacent PPUDATA reads. Passing the frame-counter ROMs also does not exhaust APU interactions, including simultaneous length-counter writes and clocks. The nonlinear TND mixer remains the documented lookup-table approximation, and the output filter is an approximation of the console's analog circuitry.

The resampler and output filters are presentation state, like queued audio. They are rebuilt from the current DAC level on reset or save-state load; their recent signal history is not serialized. Existing v2–v5 save files remain loadable with their original standard MMC3 profile, with a short filter settling interval after a discontinuity. Emulated channel state and the fractional sample clock remain serialized; v5 additionally preserves the DMC buffer and pending DMA request, and v6 identifies the MMC3 IRQ profile.
