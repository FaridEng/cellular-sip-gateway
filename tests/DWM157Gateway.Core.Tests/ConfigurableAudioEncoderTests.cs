using Concentus;
using Concentus.Enums;
using DWM157Gateway.Core.Audio;
using DWM157Gateway.Core.Configuration;
using SIPSorceryMedia.Abstractions;

namespace DWM157Gateway.Core.Tests;

public sealed class ConfigurableAudioEncoderTests
{
    private static readonly PcmFormat ModemPcm = new(8000, 1, 16, 20);

    [Fact]
    public void PlacesPreferredCodecFirstAndUsesOpusRtpClock()
    {
        using var encoder = new ConfigurableAudioEncoder(new SipConfig
        {
            PreferredCodec = "OPUS",
            EnabledCodecs = ["PCMU", "OPUS", "PCMA"],
            OpusBitrateBps = 16000,
            OpusFec = true
        }, ModemPcm);

        var opus = encoder.SupportedFormats[0];

        Assert.Equal(AudioCodecsEnum.OPUS, opus.Codec);
        Assert.Equal(8000, opus.ClockRate);
        Assert.Equal(48000, opus.RtpClockRate);
        Assert.Equal(2, opus.ChannelCount);
        Assert.Contains("useinbandfec=1", opus.Parameters);
        Assert.Contains("maxaveragebitrate=16000", opus.Parameters);
    }

    [Fact]
    public void EncodesAndDecodesTwentyMillisecondsOfNarrowbandOpus()
    {
        using var encoder = new ConfigurableAudioEncoder(new SipConfig(), ModemPcm);
        var opus = encoder.SupportedFormats.Single(format => format.Codec == AudioCodecsEnum.OPUS);
        var samplesPerFrame = ModemPcm.SampleRateHz * ModemPcm.FrameMilliseconds / 1000;
        var pcm = Enumerable.Range(0, samplesPerFrame)
            .Select(index => (short)(Math.Sin(index * 2 * Math.PI * 440 / ModemPcm.SampleRateHz) * 10000))
            .ToArray();

        var encoded = encoder.EncodeAudio(pcm, opus);
        var decoded = encoder.DecodeAudio(encoded, opus);

        Assert.NotEmpty(encoded);
        Assert.True(encoded.Length < pcm.Length * sizeof(short));
        Assert.Equal(samplesPerFrame, decoded.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DecodesFortyEightKilohertzOpusIntoNarrowbandMono(int encodedChannels)
    {
        using var gatewayDecoder = new ConfigurableAudioEncoder(new SipConfig(), ModemPcm);
        var opus = gatewayDecoder.SupportedFormats.Single(format => format.Codec == AudioCodecsEnum.OPUS);
        using var linphoneLikeEncoder = OpusCodecFactory.CreateEncoder(
            48000,
            encodedChannels,
            OpusApplication.OPUS_APPLICATION_VOIP);
        linphoneLikeEncoder.Bitrate = 38000;

        const int samplesPerChannel = 960;
        var pcm = new short[samplesPerChannel * encodedChannels];
        for (var index = 0; index < samplesPerChannel; index++)
        {
            var sample = (short)(Math.Sin(index * 2 * Math.PI * 440 / 48000) * 12000);
            for (var channel = 0; channel < encodedChannels; channel++)
            {
                pcm[index * encodedChannels + channel] = sample;
            }
        }

        var maximumDecodedSample = 0;
        Span<byte> packetBuffer = stackalloc byte[1275];
        for (var frame = 0; frame < 25; frame++)
        {
            var packetLength = linphoneLikeEncoder.Encode(pcm, samplesPerChannel, packetBuffer, packetBuffer.Length);
            var decoded = gatewayDecoder.DecodeAudio(packetBuffer[..packetLength].ToArray(), opus);

            Assert.Equal(160, decoded.Length);
            maximumDecodedSample = Math.Max(
                maximumDecodedSample,
                decoded.Max(sample => Math.Abs((int)sample)));
        }

        Assert.True(maximumDecodedSample > 1000, $"Maximum decoded sample was {maximumDecodedSample}.");
    }

    [Fact]
    public void AllowsPcmuOnlyRollbackConfiguration()
    {
        using var encoder = new ConfigurableAudioEncoder(new SipConfig
        {
            PreferredCodec = "PCMU",
            EnabledCodecs = ["PCMU", "PCMA"]
        }, ModemPcm);

        Assert.Equal([AudioCodecsEnum.PCMU, AudioCodecsEnum.PCMA], encoder.SupportedFormats.Select(format => format.Codec));
    }

    [Fact]
    public void GeneratesTwentyMillisecondsOfOpusPacketLossConcealment()
    {
        using var encoder = new ConfigurableAudioEncoder(new SipConfig(), ModemPcm);
        var opus = encoder.SupportedFormats.Single(format => format.Codec == AudioCodecsEnum.OPUS);
        var samplesPerFrame = ModemPcm.SampleRateHz * ModemPcm.FrameMilliseconds / 1000;

        var concealed = encoder.DecodeOpusPacketLoss(opus, ModemPcm.FrameMilliseconds);

        Assert.Equal(samplesPerFrame, concealed.Length);
    }

    [Fact]
    public void RestrictsFormatsToPreferredCodecWhenForced()
    {
        using var encoder = new ConfigurableAudioEncoder(new SipConfig
        {
            PreferredCodec = "OPUS",
            EnabledCodecs = ["OPUS", "PCMU", "PCMA"],
            ForcePreferredCodec = true
        }, ModemPcm);

        var format = Assert.Single(encoder.SupportedFormats);
        Assert.Equal(AudioCodecsEnum.OPUS, format.Codec);
    }

    [Fact]
    public void RejectsPreferredCodecThatIsDisabled()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ConfigurableAudioEncoder(new SipConfig
            {
                PreferredCodec = "OPUS",
                EnabledCodecs = ["PCMU", "PCMA"]
            }, ModemPcm));

        Assert.Contains("PreferredCodec", exception.Message);
    }
}
