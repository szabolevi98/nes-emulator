using System.Collections.Concurrent;
using System.Numerics;

namespace NesEmulator.Core.Apu;

/// <summary>
/// Low-pass the CPU-rate DAC signal before downsampling, then apply the NES
/// output circuit's 90/440 Hz high passes and 14 kHz low pass. Filtering only
/// after downsampling cannot remove harmonics already folded into audible tones.
/// This is presentation state, like the sample queue, rebuilt on reset/load.
/// </summary>
internal sealed class AudioResampler
{
    private const int Phases = 32;
    private static readonly ConcurrentDictionary<int, float[][]> Kernels = new();
    private readonly float[][] _kernels;
    private readonly float[] _history;
    private readonly int _taps;
    private int _position;
    private readonly OnePole _high90;
    private readonly OnePole _high440;
    private readonly OnePole _low14k;

    public AudioResampler(int sampleRate)
    {
        _kernels = Kernels.GetOrAdd(sampleRate, BuildKernels);
        _taps = _kernels[0].Length;
        // Mirroring the ring makes each convolution contiguous for SIMD.
        _history = new float[_taps * 2];
        _high90 = new(sampleRate, 90, highPass: true);
        _high440 = new(sampleRate, 440, highPass: true);
        _low14k = new(sampleRate, Math.Min(14000, sampleRate * 0.45), highPass: false);
    }

    public void Reset(float level)
    {
        Array.Fill(_history, level);
        _position = 0;
        _high90.Reset(level);
        _high440.Reset(0);
        _low14k.Reset(0);
    }

    public void Write(float level)
    {
        _history[_position] = _history[_position + _taps] = level;
        if (++_position == _taps) _position = 0;
    }

    public float Sample(double fraction)
    {
        int phase = Math.Clamp((int)(fraction * Phases), 0, Phases - 1);
        float[] kernel = _kernels[phase];
        Vector<float> sum = Vector<float>.Zero;
        int i = 0;
        for (; i <= _taps - Vector<float>.Count; i += Vector<float>.Count)
        {
            sum += new Vector<float>(_history, _position + i) * new Vector<float>(kernel, i);
        }
        float value = Vector.Sum(sum);
        for (; i < _taps; i++) value += _history[_position + i] * kernel[i];
        return _low14k.Process(_high440.Process(_high90.Process(value)));
    }

    private static float[][] BuildKernels(int sampleRate)
    {
        // A 48-output-sample Blackman-windowed sinc, with a transition band
        // between the audible passband and the output Nyquist frequency.
        int taps = (int)Math.Ceiling(Apu2A03.ClockRate / sampleRate * 48);
        double cutoff = sampleRate * 0.42 / Apu2A03.ClockRate;
        float[][] kernels = new float[Phases][];
        for (int phase = 0; phase < Phases; phase++)
        {
            float[] kernel = kernels[phase] = new float[taps];
            double total = 0;
            for (int i = 0; i < taps; i++)
            {
                double x = i - (taps - 1) / 2.0 + (double)phase / Phases;
                double sinc = Math.Abs(x) < 1e-12 ? 2 * cutoff
                    : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
                double angle = 2 * Math.PI * i / (taps - 1);
                double window = 0.42 - 0.5 * Math.Cos(angle) + 0.08 * Math.Cos(2 * angle);
                kernel[i] = (float)(sinc * window);
                total += kernel[i];
            }
            for (int i = 0; i < taps; i++) kernel[i] /= (float)total;
        }
        return kernels;
    }

    private sealed class OnePole
    {
        private readonly double _b0, _b1, _feedback;
        private double _previousInput, _previousOutput;

        public OnePole(int sampleRate, double cutoff, bool highPass)
        {
            double k = Math.Tan(Math.PI * cutoff / sampleRate);
            _b0 = (highPass ? 1 : k) / (1 + k);
            _b1 = highPass ? -_b0 : _b0;
            _feedback = (1 - k) / (1 + k);
        }

        public void Reset(float input) { _previousInput = input; _previousOutput = 0; }

        public float Process(float input)
        {
            double output = _b0 * input + _b1 * _previousInput + _feedback * _previousOutput;
            _previousInput = input;
            _previousOutput = output;
            return (float)output;
        }
    }
}
