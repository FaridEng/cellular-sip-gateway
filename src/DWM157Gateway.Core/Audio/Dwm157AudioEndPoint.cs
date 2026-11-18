using System.Net;
using DWM157Gateway.Core.Configuration;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace DWM157Gateway.Core.Audio;

public sealed class Dwm157AudioEndPoint : IAudioSource, IAudioSink
{
    private const int MaximumPlcFramesPerGap = 3;
    private readonly IVoicePcmTransport _voiceTransport;
    private readonly ConfigurableAudioEncoder _audioEncoder;
    private readonly MediaFormatManager<AudioFormat> _formatManager;
    private readonly PcmFormat _pcmFormat;
    private readonly AdaptivePcmFrameBuffer _modemToRtpBuffer;
    private readonly AdaptivePcmFrameBuffer _rtpToModemBuffer;
    private readonly PcmGainProcessor _modemToRtpGain;
    private readonly PcmGainProcessor _rtpToModemLevel = new(0);
    private readonly EncodedPayloadStatistics _encodedPayloadStatistics = new();
    private readonly AudioPacingConfig _pacingConfig;
    private readonly object _rtpDecodeSync = new();
    private CancellationTokenSource? _audioCts;
    private AudioFormat? _selectedFormat;
    private AudioFramePacer? _rtpSendPacer;
    private AudioFramePacer? _modemWritePacer;
    private uint? _lastRtpSsrc;
    private ushort? _lastRtpSequence;
    private long _missingRtpPackets;
    private long _fecRecoveryFrames;
    private long _plcRecoveryFrames;
    private long _skippedRecoveryFrames;
    private long _duplicateRtpPackets;
    private long _lateRtpPackets;

    public Dwm157AudioEndPoint(
        IVoicePcmTransport voiceTransport,
        PcmFormat pcmFormat,
        SipConfig sipConfig,
        JitterBufferConfig? rtpToModemBuffer = null,
        JitterBufferConfig? modemToRtpBuffer = null,
        AudioPacingConfig? pacingConfig = null,
        double modemToRtpGainDb = 0)
    {
        _voiceTransport = voiceTransport;
        _pcmFormat = pcmFormat;
        _audioEncoder = new ConfigurableAudioEncoder(sipConfig, pcmFormat);
        _modemToRtpBuffer = new AdaptivePcmFrameBuffer(
            pcmFormat.FrameMilliseconds,
            NormalizeJitterBufferConfig(modemToRtpBuffer));
        _rtpToModemBuffer = new AdaptivePcmFrameBuffer(
            pcmFormat.FrameMilliseconds,
            NormalizeJitterBufferConfig(rtpToModemBuffer),
            rebufferAfterUnderrun: false);
        _modemToRtpGain = new PcmGainProcessor(modemToRtpGainDb);
        _pacingConfig = pacingConfig ?? new AudioPacingConfig();
        _formatManager = new MediaFormatManager<AudioFormat>(_audioEncoder.SupportedFormats);
    }

    public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
    public event Action<EncodedAudioFrame>? OnAudioSourceEncodedFrameReady;
#pragma warning disable CS0067
    public event RawAudioSampleDelegate? OnAudioSourceRawSample;
    public event SourceErrorDelegate? OnAudioSourceError;
    public event SourceErrorDelegate? OnAudioSinkError;
#pragma warning restore CS0067

    public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample is not null;
    public bool IsAudioSourcePaused() => _audioCts is null;
    public string SelectedCodec => _selectedFormat?.Codec.ToString() ?? "not-negotiated";
    public int SelectedRtpClockRate => _selectedFormat?.RtpClockRate ?? _pcmFormat.SampleRateHz;
    public List<AudioFormat> GetAudioSourceFormats() => _formatManager.GetSourceFormats();
    public List<AudioFormat> GetAudioSinkFormats() => _formatManager.GetSourceFormats();
    public void RestrictFormats(Func<AudioFormat, bool> filter) => _formatManager.RestrictFormats(filter);
    public void SetAudioSourceFormat(AudioFormat audioFormat) => _selectedFormat = audioFormat;
    public void SetAudioSinkFormat(AudioFormat audioFormat) => _selectedFormat = audioFormat;

    public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
    {
        throw new NotSupportedException("The DWM-157 audio endpoint is driven by the modem Service Port.");
    }

    public Task StartAudio() => StartAudioAsync();
    public Task StartAudioSource() => StartAudioAsync();
    public Task StartAudioSink() => StartAudioAsync();
    public Task PauseAudio() => Task.CompletedTask;
    public Task ResumeAudio() => StartAudioAsync();
    public Task PauseAudioSink() => Task.CompletedTask;
    public Task ResumeAudioSink() => StartAudioAsync();

    public Task CloseAudio() => CloseAudioAsync();
    public Task CloseAudioSource() => CloseAudioAsync();
    public Task CloseAudioSink() => CloseAudioAsync();

    public void GotAudioRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[] payload)
    {
        if (_selectedFormat is null)
        {
            return;
        }

        lock (_rtpDecodeSync)
        {
            var format = _selectedFormat.Value;
            if (format.Codec != AudioCodecsEnum.OPUS)
            {
                QueuePcm(_audioEncoder.DecodeAudio(payload, format));
                return;
            }

            var sequence = (ushort)seqnum;
            if (_lastRtpSsrc != ssrc || _lastRtpSequence is null)
            {
                _lastRtpSsrc = ssrc;
                _lastRtpSequence = sequence;
                QueuePcm(_audioEncoder.DecodeAudio(payload, format));
                return;
            }

            var sequenceDelta = (ushort)(sequence - _lastRtpSequence.Value);
            if (sequenceDelta == 0)
            {
                Interlocked.Increment(ref _duplicateRtpPackets);
                return;
            }

            if (sequenceDelta >= 0x8000)
            {
                Interlocked.Increment(ref _lateRtpPackets);
                return;
            }

            var missingPackets = sequenceDelta - 1;
            if (missingPackets == 1)
            {
                QueuePcm(_audioEncoder.DecodeOpusRecovery(payload, format, _pcmFormat.FrameMilliseconds));
                Interlocked.Increment(ref _fecRecoveryFrames);
            }
            else if (missingPackets > 1)
            {
                var plcFrames = Math.Min(missingPackets, MaximumPlcFramesPerGap);
                for (var index = 0; index < plcFrames; index++)
                {
                    QueuePcm(_audioEncoder.DecodeOpusPacketLoss(format, _pcmFormat.FrameMilliseconds));
                }

                Interlocked.Add(ref _plcRecoveryFrames, plcFrames);
                Interlocked.Add(ref _skippedRecoveryFrames, missingPackets - plcFrames);
            }

            Interlocked.Add(ref _missingRtpPackets, missingPackets);
            _lastRtpSequence = sequence;
            QueuePcm(_audioEncoder.DecodeAudio(payload, format));
        }
    }

    public void GotEncodedMediaFrame(EncodedAudioFrame encodedMediaFrame)
    {
        var pcm = _audioEncoder.DecodeAudio(encodedMediaFrame.EncodedAudio, encodedMediaFrame.AudioFormat);
        QueuePcm(pcm);
    }

    public MediaEndPoints ToMediaEndPoints() => new()
    {
        AudioSource = this,
        AudioSink = this
    };

    public AudioBufferSnapshot GetBufferSnapshot()
    {
        var modemToRtpTiming = _modemToRtpBuffer.GetTimingSnapshot();
        var rtpToModemTiming = _rtpToModemBuffer.GetTimingSnapshot();
        return new AudioBufferSnapshot(
            _modemToRtpBuffer.DroppedFrames,
            _modemToRtpBuffer.UnderrunFrames,
            _modemToRtpBuffer.TargetBufferMilliseconds,
            _modemToRtpBuffer.BufferedFrames,
            modemToRtpTiming,
            _rtpToModemBuffer.DroppedFrames,
            _rtpToModemBuffer.UnderrunFrames,
            _rtpToModemBuffer.TargetBufferMilliseconds,
            _rtpToModemBuffer.BufferedFrames,
            rtpToModemTiming);
    }

    public AudioBridgePacingSnapshot GetPacingSnapshot() => new(
        _rtpSendPacer?.GetSnapshot() ?? EmptyPacingSnapshot(),
        _modemWritePacer?.GetSnapshot() ?? EmptyPacingSnapshot());

    public PcmGainSnapshot GetModemToRtpGainSnapshot() => _modemToRtpGain.GetSnapshot();
    public PcmGainSnapshot GetRtpToModemLevelSnapshot() => _rtpToModemLevel.GetSnapshot();
    public OpusRecoverySnapshot GetOpusRecoverySnapshot() => new(
        Interlocked.Read(ref _missingRtpPackets),
        Interlocked.Read(ref _fecRecoveryFrames),
        Interlocked.Read(ref _plcRecoveryFrames),
        Interlocked.Read(ref _skippedRecoveryFrames),
        Interlocked.Read(ref _duplicateRtpPackets),
        Interlocked.Read(ref _lateRtpPackets));
    public EncodedPayloadSnapshot GetEncodedPayloadSnapshot() => _encodedPayloadStatistics.GetSnapshot();

    private Task StartAudioAsync()
    {
        if (_audioCts is not null)
        {
            return Task.CompletedTask;
        }

        var formats = _formatManager.GetSourceFormats();
        if (_selectedFormat is null)
        {
            if (formats.Count == 0)
            {
                throw new InvalidOperationException("No RTP audio codec is available.");
            }

            _selectedFormat = formats[0];
        }
        _audioCts = new CancellationTokenSource();
        _modemToRtpBuffer.Clear();
        _rtpToModemBuffer.Clear();
        lock (_rtpDecodeSync)
        {
            _audioEncoder.ResetState();
            _lastRtpSsrc = null;
            _lastRtpSequence = null;
            Interlocked.Exchange(ref _missingRtpPackets, 0);
            Interlocked.Exchange(ref _fecRecoveryFrames, 0);
            Interlocked.Exchange(ref _plcRecoveryFrames, 0);
            Interlocked.Exchange(ref _skippedRecoveryFrames, 0);
            Interlocked.Exchange(ref _duplicateRtpPackets, 0);
            Interlocked.Exchange(ref _lateRtpPackets, 0);
        }
        _ = Task.Run(() => ReadModemAudioAsync(_audioCts.Token), CancellationToken.None);
        _ = Task.Run(() => SendModemAudioAsync(_audioCts.Token), CancellationToken.None);
        _ = Task.Run(() => WriteModemAudioAsync(_audioCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task SendModemAudioAsync(CancellationToken cancellationToken)
    {
        try
        {
            var selectedFormat = _selectedFormat
                ?? throw new InvalidOperationException("No RTP audio codec was selected.");

            await _modemToRtpBuffer.WaitUntilStartedAsync(cancellationToken);
            using var pacer = new AudioFramePacer(_pcmFormat.FrameMilliseconds, _pacingConfig);
            _rtpSendPacer = pacer;
            byte[]? lastFrame = null;
            var consecutiveLosses = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                await pacer.WaitForNextFrameAsync(cancellationToken);
                byte[] frame;
                if (_modemToRtpBuffer.TryRead(out var bufferedFrame))
                {
                    frame = bufferedFrame!;
                    lastFrame = frame;
                    consecutiveLosses = 0;
                }
                else
                {
                    _modemToRtpBuffer.ReportUnderrun();
                    frame = CreateConcealmentFrame(lastFrame, ++consecutiveLosses);
                }

                var pcm = new short[frame.Length / sizeof(short)];
                Buffer.BlockCopy(frame, 0, pcm, 0, frame.Length);
                _modemToRtpGain.ApplyInPlace(pcm);
                var encoded = _audioEncoder.EncodeAudio(pcm, selectedFormat);
                _encodedPayloadStatistics.Record(encoded.Length);
                var durationRtpUnits = (uint)(selectedFormat.RtpClockRate * _pcmFormat.FrameMilliseconds / 1000);
                OnAudioSourceEncodedSample?.Invoke(durationRtpUnits, encoded);
                OnAudioSourceEncodedFrameReady?.Invoke(new EncodedAudioFrame(0, selectedFormat, (uint)_pcmFormat.FrameMilliseconds, encoded));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            OnAudioSourceError?.Invoke(ex.Message);
        }
    }

    private Task CloseAudioAsync()
    {
        _audioCts?.Cancel();
        _audioCts?.Dispose();
        _audioCts = null;
        return Task.CompletedTask;
    }

    private void QueuePcm(short[] pcm)
    {
        _rtpToModemLevel.ApplyInPlace(pcm);
        var bytes = new byte[pcm.Length * sizeof(short)];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);

        for (var offset = 0; offset + _pcmFormat.FrameBytes <= bytes.Length; offset += _pcmFormat.FrameBytes)
        {
            _rtpToModemBuffer.Enqueue(bytes.AsSpan(offset, _pcmFormat.FrameBytes).ToArray());
        }
    }

    private async Task WriteModemAudioAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _rtpToModemBuffer.WaitUntilStartedAsync(cancellationToken);
            using var pacer = new AudioFramePacer(_pcmFormat.FrameMilliseconds, _pacingConfig);
            _modemWritePacer = pacer;
            byte[]? lastFrame = null;
            var consecutiveLosses = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                await pacer.WaitForNextFrameAsync(cancellationToken);
                byte[] frame;
                if (_rtpToModemBuffer.TryRead(out var bufferedFrame))
                {
                    frame = bufferedFrame!;
                    lastFrame = frame;
                    consecutiveLosses = 0;
                }
                else
                {
                    _rtpToModemBuffer.ReportUnderrun();
                    frame = CreateConcealmentFrame(lastFrame, ++consecutiveLosses);
                }

                await _voiceTransport.WriteFrameAsync(frame, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            OnAudioSinkError?.Invoke(ex.Message);
        }
    }

    private byte[] CreateConcealmentFrame(byte[]? lastFrame, int consecutiveLosses)
    {
        if (lastFrame is null || consecutiveLosses > 3)
        {
            return new byte[_pcmFormat.FrameBytes];
        }

        var concealed = new byte[lastFrame.Length];
        var attenuation = Math.Pow(0.65, consecutiveLosses);
        for (var offset = 0; offset + sizeof(short) <= lastFrame.Length; offset += sizeof(short))
        {
            var sample = BitConverter.ToInt16(lastFrame, offset);
            var attenuated = (short)Math.Clamp(sample * attenuation, short.MinValue, short.MaxValue);
            BitConverter.TryWriteBytes(concealed.AsSpan(offset, sizeof(short)), attenuated);
        }

        return concealed;
    }

    private async Task ReadModemAudioAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _voiceTransport.RxFrames.WaitToReadAsync(cancellationToken))
            {
                while (_voiceTransport.RxFrames.TryRead(out var frame))
                {
                    _modemToRtpBuffer.Enqueue(frame);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static JitterBufferConfig NormalizeJitterBufferConfig(JitterBufferConfig? config)
    {
        if (config is null || config.Enabled)
        {
            return config ?? new JitterBufferConfig();
        }

        return new JitterBufferConfig
        {
            Enabled = false,
            MinBufferMilliseconds = 0,
            TargetBufferMilliseconds = 0,
            MaxBufferMilliseconds = 20,
            StableFramesBeforeShrink = 1
        };
    }

    private static AudioPacingSnapshot EmptyPacingSnapshot() =>
        new(0, 0, 0, 0, 0, 0, 0, 0);
}

public sealed record AudioBufferSnapshot(
    long ModemToRtpDroppedFrames,
    long ModemToRtpUnderrunFrames,
    int ModemToRtpTargetMilliseconds,
    int ModemToRtpBufferedFrames,
    PcmFrameBufferTimingSnapshot ModemToRtpTiming,
    long RtpToModemDroppedFrames,
    long RtpToModemUnderrunFrames,
    int RtpToModemTargetMilliseconds,
    int RtpToModemBufferedFrames,
    PcmFrameBufferTimingSnapshot RtpToModemTiming);

public sealed record AudioBridgePacingSnapshot(
    AudioPacingSnapshot RtpSend,
    AudioPacingSnapshot ModemWrite);

public sealed record OpusRecoverySnapshot(
    long MissingPackets,
    long FecRecoveryFrames,
    long PlcRecoveryFrames,
    long SkippedRecoveryFrames,
    long DuplicatePackets,
    long LatePackets);
