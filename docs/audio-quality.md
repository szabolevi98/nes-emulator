# Audio quality

The earlier output sampled the mixer directly about once every 40 CPU cycles. High-frequency waveform harmonics could fold into audible frequencies, and the playback backend only removed a slowly tracked DC offset. The noise period table was also treated as CPU/2 ticks plus one, even though its entries already specified complete CPU-cycle periods. That made percussion slower and lower than intended.

The signal path now is:

1. Clock the channels and mix their DAC levels every CPU cycle.
2. Apply a Blackman-windowed sinc low-pass filter before converting to the requested sample rate. The kernel spans 48 output samples, with 32 fractional CPU-cycle phases and SIMD convolution. This adds about 0.54 ms of filter delay at 44.1 kHz.
3. Apply first-order high-pass filters at 90 and 440 Hz, then a low-pass at 14 kHz, approximating the [NES output circuit](https://www.nesdev.org/wiki/APU_Mixer).
4. Convert signed PCM to 16-bit audio in the Windows backend. The host checks buffer availability before removing samples from the APU queue.

The [noise period table](https://www.nesdev.org/wiki/APU_Noise) is converted to CPU/2 timer units, including the zero tick. DMC timers likewise include their zero tick, fixing the extra CPU cycle per bit. DMC looping now restarts the reader when the last byte is fetched, removing an extra silent byte between repetitions and preventing disabled loops from restarting. The triangle sequencer continues at ultrasonic periods as [the hardware does](https://www.nesdev.org/wiki/APU_Triangle); the resampler removes those frequencies.

## Validation

Run `dotnet run -c Release --project tests/NesEmulator.Tests` for the 239 offline checks. Audio regressions cover all 16 noise periods against a CPU-clocked reference sequence, DMC bit spacing and continuous looping, stopping a loop, and the following at both 44.1 and 48 kHz:

- A 1 kHz signal remains audible with the expected filter gain.
- A 30 Hz signal is attenuated by the output high-pass filters.
- A signal 1 kHz above output Nyquist is rejected by more than 46 dB relative to the 1 kHz reference, instead of appearing as an audible alias.
- Resetting the filter to a held DAC level does not produce a startup impulse.

The public `8-dmc_rates` ROM now passes; the full public suite passes 38/55. All previously passing ROMs remain passing. The complete results, including failures, are in [accuracy-results.md](accuracy-results.md).

Local runs with the user's Mega Man 4 and Super Mario Bros. 3 images also generated before/after PCM recordings. These ROMs and recordings are excluded from version control. This validates the output path with real game code; it is not a comparison against a physical console recording.

## Remaining limits

This is not yet a cycle-perfect APU. Frame-counter write delays and IRQ edge timing still fail public tests. The DMC does not yet implement its separate prefetch buffer or arbitrate CPU bus ownership; `7-dmc_basics` reaches the missing one-byte buffer check. The nonlinear TND mixer remains the documented lookup-table approximation, and the output filter is an approximation of the console's analog circuitry.

The resampler and output filters are presentation state, like queued audio. They are rebuilt from the current DAC level on reset or save-state load; their recent signal history is not serialized. Existing v2 save files remain loadable, with a short filter settling interval after a discontinuity. Emulated channel state and the fractional sample clock remain serialized.
