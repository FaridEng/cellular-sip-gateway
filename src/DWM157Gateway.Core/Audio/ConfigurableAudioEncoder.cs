using Concentus;
using Concentus.Enums;
using DWM157Gateway.Core.Configuration;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace DWM157Gateway.Core.Audio;

public sealed class ConfigurableAudioEncoder : IAudioEncoder, IDisposable
{
    private const int OpusPayloadType = 111;
    private const int OpusRtpClockRate = 48000;
    private const int OpusSdpChannels = 2;
    private const int OpusResamplerQuality = 5;
    private const int MaxOpusPacketBytes = 1275;
    private static readonly string[] KnownCodecNames = ["OPUS", "PCMU", "PCMA"];

    private readonly AudioEncoder _g711Encoder = new();
    private readonly IOpusEncoder? _opusEncoder;
    private readonly IOpusDecoder? _opusDecoder;
    private readonly IResampler? _opusDecodeResampler;
    private readonly int _sampleRateHz;
    private readonly int _channels;

    public ConfigurableAudioEncoder(SipConfig config, PcmFormat pcmFormat)
    {
        ArgumentNullException.ThrowIfNull(config);
        Validate(config, pcmFormat);

        _sampleRateHz = pcmFormat.SampleRateHz;
        _channels = pcmFormat.Channels;
        SupportedFormats = BuildFormats(config, pcmFormat.SampleRateHz);

        if (SupportedFormats.Any(format => format.Codec == AudioCodecsEnum.OPUS))
        {
            _opusEncoder = OpusCodecFactory.CreateEncoder(
                pcmFormat.SampleRateHz,
                pcmFormat.Channels,
                OpusApplication.OPUS_APPLICATION_VOIP);
            _opusEncoder.Bitrate = config.OpusBitrateBps;
            _opusEncoder.UseInbandFEC = config.OpusFec;
            _opusEncoder.PacketLossPercent = config.OpusExpectedPacketLossPercent;
            _opusEncoder.UseDTX = config.OpusDtx;
            _opusEncoder.Complexity = config.OpusComplexity;
            _opusEncoder.UseVBR = config.OpusVbr;

            // Some fullband Opus streams decode as silence when Concentus is asked to
            // produce narrowband PCM directly. Decode at the RTP clock and resample.
            _opusDecoder = OpusCodecFactory.CreateDecoder(OpusRtpClockRate, pcmFormat.Channels);
            if (pcmFormat.SampleRateHz != OpusRtpClockRate)
            {
                _opusDecodeResampler = ResamplerFactory.CreateResampler(
                    pcmFormat.Channels,
                    OpusRtpClockRate,
                    pcmFormat.SampleRateHz,
                    OpusResamplerQuality);
            }
        }
    }

    public List<AudioFormat> SupportedFormats { get; }

    public byte[] EncodeAudio(short[] pcm, AudioFormat format)
    {
        if (format.Codec != AudioCodecsEnum.OPUS)
        {
            return _g711Encoder.EncodeAudio(pcm, format);
        }

        if (_opusEncoder is null)
        {
            throw new InvalidOperationException("OPUS is not enabled in the gateway configuration.");
        }

        if (pcm.Length == 0 || pcm.Length % _channels != 0)
        {
            throw new ArgumentException("OPUS PCM input must contain complete channel samples.", nameof(pcm));
        }

        Span<byte> encoded = stackalloc byte[MaxOpusPacketBytes];
        var encodedLength = _opusEncoder.Encode(pcm, pcm.Length / _channels, encoded, encoded.Length);
        return encoded[..encodedLength].ToArray();
    }

    public short[] DecodeAudio(byte[] encodedSample, AudioFormat format)
    {
        if (format.Codec != AudioCodecsEnum.OPUS)
        {
            return _g711Encoder.DecodeAudio(encodedSample, format);
        }

        if (_opusDecoder is null)
        {
            throw new InvalidOperationException("OPUS is not enabled in the gateway configuration.");
        }

        // Opus can contain up to 120 ms per packet. The decoder returns the actual sample count.
        var decoded = new short[OpusRtpClockRate * 120 / 1000 * _channels];
        var samplesPerChannel = _opusDecoder.Decode(encodedSample, decoded, decoded.Length / _channels, false);
        return ResampleDecodedOpus(decoded, samplesPerChannel);
    }

    public short[] DecodeOpusRecovery(byte[] encodedSample, AudioFormat format, int frameMilliseconds)
    {
        EnsureOpus(format);
        ValidateRecoveryFrameMilliseconds(frameMilliseconds);
        var samplesPerChannel = OpusRtpClockRate * frameMilliseconds / 1000;
        var decoded = new short[samplesPerChannel * _channels];
        var decodedSamples = _opusDecoder!.Decode(encodedSample, decoded, samplesPerChannel, true);
        return ResampleDecodedOpus(decoded, decodedSamples);
    }

    public short[] DecodeOpusPacketLoss(AudioFormat format, int frameMilliseconds)
    {
        EnsureOpus(format);
        ValidateRecoveryFrameMilliseconds(frameMilliseconds);
        var samplesPerChannel = OpusRtpClockRate * frameMilliseconds / 1000;
        var decoded = new short[samplesPerChannel * _channels];
        var decodedSamples = _opusDecoder!.Decode(ReadOnlySpan<byte>.Empty, decoded, samplesPerChannel, false);
        return ResampleDecodedOpus(decoded, decodedSamples);
    }

    public void ResetState()
    {
        _opusEncoder?.ResetState();
        _opusDecoder?.ResetState();
        _opusDecodeResampler?.ResetMem();
    }

    public void Dispose()
    {
        (_opusEncoder as IDisposable)?.Dispose();
        (_opusDecoder as IDisposable)?.Dispose();
        (_opusDecodeResampler as IDisposable)?.Dispose();
        _g711Encoder.Dispose();
    }

    private static List<AudioFormat> BuildFormats(SipConfig config, int sampleRateHz)
    {
        var enabled = config.EnabledCodecs
            .Select(NormalizeCodecName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var preferred = NormalizeCodecName(config.PreferredCodec);

        if (config.ForcePreferredCodec)
        {
            enabled = [preferred];
        }

        var ordered = new[] { preferred }
            .Concat(enabled)
            .Where(codec => enabled.Contains(codec, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return ordered.Select(codec => codec switch
        {
            "OPUS" => new AudioFormat(
                AudioCodecsEnum.OPUS,
                OpusPayloadType,
                sampleRateHz,
                OpusRtpClockRate,
                OpusSdpChannels,
                $"useinbandfec={(config.OpusFec ? 1 : 0)};maxaveragebitrate={config.OpusBitrateBps}"),
            "PCMU" => new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU),
            "PCMA" => new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA),
            _ => throw new InvalidOperationException($"Unsupported audio codec '{codec}'.")
        }).ToList();
    }

    private static string NormalizeCodecName(string? codec)
    {
        return codec?.Trim().ToUpperInvariant() ?? string.Empty;
    }

    private void EnsureOpus(AudioFormat format)
    {
        if (format.Codec != AudioCodecsEnum.OPUS || _opusDecoder is null)
        {
            throw new InvalidOperationException("OPUS recovery requires an enabled and selected OPUS codec.");
        }
    }

    private short[] ResampleDecodedOpus(short[] decoded, int samplesPerChannel)
    {
        if (_opusDecodeResampler is null)
        {
            return decoded[..(samplesPerChannel * _channels)];
        }

        var outputCapacityPerChannel =
            (samplesPerChannel * _sampleRateHz + OpusRtpClockRate - 1) / OpusRtpClockRate + 32;
        var output = new short[outputCapacityPerChannel * _channels];
        var inputLengthPerChannel = samplesPerChannel;
        var outputLengthPerChannel = outputCapacityPerChannel;
        _opusDecodeResampler.ProcessInterleaved(
            decoded.AsSpan(0, samplesPerChannel * _channels),
            ref inputLengthPerChannel,
            output,
            ref outputLengthPerChannel);

        if (inputLengthPerChannel != samplesPerChannel)
        {
            throw new InvalidOperationException("The Opus decode resampler did not consume the complete PCM frame.");
        }

        return output[..(outputLengthPerChannel * _channels)];
    }

    private static void ValidateRecoveryFrameMilliseconds(int frameMilliseconds)
    {
        if (frameMilliseconds <= 0 || frameMilliseconds % 10 != 0 || frameMilliseconds > 120)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameMilliseconds),
                "OPUS recovery duration must be a 10ms multiple up to 120ms.");
        }
    }

    private static void Validate(SipConfig config, PcmFormat pcmFormat)
    {
        if (config.EnabledCodecs.Count == 0)
        {
            throw new InvalidOperationException("Sip.EnabledCodecs must contain at least one codec.");
        }

        var unknown = config.EnabledCodecs
            .Append(config.PreferredCodec)
            .Select(NormalizeCodecName)
            .Where(codec => !KnownCodecNames.Contains(codec, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException($"Unsupported Sip codec(s): {string.Join(", ", unknown)}.");
        }

        if (!config.EnabledCodecs.Any(codec =>
                string.Equals(NormalizeCodecName(codec), NormalizeCodecName(config.PreferredCodec), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Sip.PreferredCodec must also be present in Sip.EnabledCodecs.");
        }

        if (pcmFormat.Channels != 1 || pcmFormat.BitsPerSample != 16)
        {
            throw new InvalidOperationException("The modem audio codec path currently requires mono 16-bit PCM.");
        }

        if (config.EnabledCodecs.Any(codec => NormalizeCodecName(codec) == "OPUS"))
        {
            if (pcmFormat.SampleRateHz is not (8000 or 12000 or 16000 or 24000 or 48000))
            {
                throw new InvalidOperationException("OPUS requires a PCM sample rate of 8000, 12000, 16000, 24000, or 48000 Hz.");
            }

            if (config.OpusBitrateBps is < 6144 or > 128000)
            {
                throw new InvalidOperationException("Sip.OpusBitrateBps must be between 6144 and 128000.");
            }

            if (config.OpusExpectedPacketLossPercent is < 0 or > 100)
            {
                throw new InvalidOperationException("Sip.OpusExpectedPacketLossPercent must be between 0 and 100.");
            }

            if (config.OpusComplexity is < 0 or > 10)
            {
                throw new InvalidOperationException("Sip.OpusComplexity must be between 0 and 10.");
            }
        }
    }
}
