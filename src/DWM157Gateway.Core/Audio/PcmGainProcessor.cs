namespace DWM157Gateway.Core.Audio;

public sealed class PcmGainProcessor
{
    private const double FullScale = 32768.0;
    private const double ActiveFrameThresholdDbfs = -45.0;
    private readonly object _sync = new();
    private readonly double _gainFactor;
    private long _processedFrames;
    private long _processedSamples;
    private long _activeFrames;
    private long _activeSamples;
    private double _activeInputSumSquares;
    private double _activeOutputSumSquares;
    private int _inputPeak;
    private int _outputPeak;
    private long _clippedSamples;

    public PcmGainProcessor(double gainDb)
    {
        if (!double.IsFinite(gainDb) || gainDb is < -24 or > 24)
        {
            throw new ArgumentOutOfRangeException(nameof(gainDb), "PCM gain must be between -24dB and +24dB.");
        }

        GainDb = gainDb;
        _gainFactor = Math.Pow(10, gainDb / 20.0);
    }

    public double GainDb { get; }

    public void ApplyInPlace(Span<short> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        double inputSumSquares = 0;
        double outputSumSquares = 0;
        var inputPeak = 0;
        var outputPeak = 0;
        long clippedSamples = 0;

        for (var index = 0; index < samples.Length; index++)
        {
            var input = samples[index];
            var inputMagnitude = Math.Abs((int)input);
            inputPeak = Math.Max(inputPeak, inputMagnitude);
            inputSumSquares += (double)input * input;

            var scaled = input * _gainFactor;
            short output;
            if (scaled > short.MaxValue)
            {
                output = short.MaxValue;
                clippedSamples++;
            }
            else if (scaled < short.MinValue)
            {
                output = short.MinValue;
                clippedSamples++;
            }
            else
            {
                output = (short)Math.Round(scaled, MidpointRounding.AwayFromZero);
            }

            samples[index] = output;
            var outputMagnitude = Math.Abs((int)output);
            outputPeak = Math.Max(outputPeak, outputMagnitude);
            outputSumSquares += (double)output * output;
        }

        var inputRms = Math.Sqrt(inputSumSquares / samples.Length);
        var isActiveFrame = ToDbfs(inputRms) >= ActiveFrameThresholdDbfs;
        lock (_sync)
        {
            _processedFrames++;
            _processedSamples += samples.Length;
            _inputPeak = Math.Max(_inputPeak, inputPeak);
            _outputPeak = Math.Max(_outputPeak, outputPeak);
            _clippedSamples += clippedSamples;
            if (isActiveFrame)
            {
                _activeFrames++;
                _activeSamples += samples.Length;
                _activeInputSumSquares += inputSumSquares;
                _activeOutputSumSquares += outputSumSquares;
            }
        }
    }

    public PcmGainSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            double? activeInputRms = _activeSamples == 0
                ? null
                : ToDbfs(Math.Sqrt(_activeInputSumSquares / _activeSamples));
            double? activeOutputRms = _activeSamples == 0
                ? null
                : ToDbfs(Math.Sqrt(_activeOutputSumSquares / _activeSamples));
            return new PcmGainSnapshot(
                GainDb,
                _processedFrames,
                _processedSamples,
                _activeFrames,
                activeInputRms,
                activeOutputRms,
                ToDbfs(_inputPeak),
                ToDbfs(_outputPeak),
                _clippedSamples,
                _processedSamples == 0 ? 0 : _clippedSamples * 100.0 / _processedSamples);
        }
    }

    private static double ToDbfs(double value) =>
        value <= 0 ? -120 : 20 * Math.Log10(value / FullScale);
}

public sealed record PcmGainSnapshot(
    double GainDb,
    long ProcessedFrames,
    long ProcessedSamples,
    long ActiveFrames,
    double? ActiveInputRmsDbfs,
    double? ActiveOutputRmsDbfs,
    double InputPeakDbfs,
    double OutputPeakDbfs,
    long ClippedSamples,
    double ClippedSamplePercent);
