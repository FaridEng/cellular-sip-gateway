namespace DWM157Gateway.Core.Audio;

public sealed record PcmFormat(int SampleRateHz, int Channels, int BitsPerSample, int FrameMilliseconds)
{
    public static PcmFormat Dwm157Default { get; } = new(8000, 1, 16, 20);
    public int FrameBytes => SampleRateHz * Channels * (BitsPerSample / 8) * FrameMilliseconds / 1000;
}
