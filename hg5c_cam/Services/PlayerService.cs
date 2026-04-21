using FFmpeg.AutoGen;
using NAudio.Wave;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace hg5c_cam.Services;

public class PlayerService
{
    private const int DefaultRtspIoTimeoutMicroseconds = 5_000_000;

    private Image? _videoImage;
    private WriteableBitmap? _writeableBitmap;
    private byte[]? _frameBuffer;
    private CancellationTokenSource? _cts;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _audioBuffer;
    private long _frameCounter;
    private long _packetBytesCounter;
    private float? _streamFps;
    private double? _streamAspectRatio;
    private int _renderScheduled;
    private int _playbackGeneration;
    private int _currentConnectionAttempt;

    public PlayerState State { get; private set; } = PlayerState.Stopped;
    public event Action<PlayerState>? StateChanged;
    public event Action<double>? StreamAspectRatioChanged;

    private static string T(string key) => LocalizationService.TranslateCurrent(key);

    public void Initialize(Image videoImage)
    {
        this._videoImage = videoImage;
    }

    public void Play(string rtspUrl, int maxFps, int reconnectDelaySec, int retries, int networkTimeoutSec, bool soundEnabled, string audioOutputDeviceName, int soundLevel)
    {
        Stop();
        var generation = Interlocked.Increment(ref this._playbackGeneration);
        this._cts = new CancellationTokenSource();
        _ = RunLoopAsync(rtspUrl, maxFps, reconnectDelaySec, retries, networkTimeoutSec, soundEnabled, audioOutputDeviceName, soundLevel, generation, this._cts.Token).ContinueWith(_ =>
        {
            SetState(PlayerState.Disconnected, generation);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    public void Stop()
    {
        Interlocked.Increment(ref this._playbackGeneration);
        if (this._cts is null) return;
        SetState(PlayerState.Disconnecting);
        this._cts.Cancel();
        this._cts.Dispose();
        this._cts = null;
        DisposeAudioOutput();
        SetState(PlayerState.Stopped);
    }

    public IReadOnlyList<string> GetAudioOutputDevices()
    {
        var devices = new List<string>();
        for (var i = 0; i < WaveOut.DeviceCount; i++)
        {
            devices.Add(WaveOut.GetCapabilities(i).ProductName);
        }

        return devices;
    }

    public float? GetStreamFps() => this._streamFps;
    public double? GetStreamAspectRatio() => this._streamAspectRatio;
    public int GetCurrentConnectionAttempt() => Volatile.Read(ref this._currentConnectionAttempt);
    public long ConsumeFrameCount() => Interlocked.Exchange(ref this._frameCounter, 0);
    public long ConsumePacketBytes() => Interlocked.Exchange(ref this._packetBytesCounter, 0);

    private async Task RunLoopAsync(string rtspUrl, int maxFps, int reconnectDelaySec, int retries, int networkTimeoutSec, bool soundEnabled, string audioOutputDeviceName, int soundLevel, int generation, CancellationToken token)
    {
        try
        {
            var attempt = 0;
            while (!token.IsCancellationRequested)
            {
                Volatile.Write(ref this._currentConnectionAttempt, attempt + 1);
                SetState(PlayerState.Connecting, generation);
                await Task.Delay(700, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(rtspUrl) || !rtspUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
                {
                    attempt++;
                    if (retries >= 0 && attempt > retries)
                    {
                        SetState(PlayerState.Disconnected, generation);
                        return;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, reconnectDelaySec)), token).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await Task.Run(() => DecodeRtspLoop(rtspUrl, maxFps, networkTimeoutSec, soundEnabled, audioOutputDeviceName, soundLevel, generation, token), token).ConfigureAwait(false);
                    attempt = 0;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    attempt++;
                    if (retries >= 0 && attempt > retries)
                    {
                        SetState(PlayerState.Disconnected, generation);
                        return;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, reconnectDelaySec)), token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            SetState(PlayerState.Disconnected, generation);
        }
    }

    private unsafe void DecodeRtspLoop(string rtspUrl, int maxFps, int networkTimeoutSec, bool soundEnabled, string audioOutputDeviceName, int soundLevel, int generation, CancellationToken token)
    {
        AVFormatContext* formatContext = null;
        AVCodecContext* videoCodecContext = null;
        AVCodecContext* audioCodecContext = null;
        AVFrame* decodedVideoFrame = null;
        AVFrame* decodedAudioFrame = null;
        AVPacket* packet = null;
        SwsContext* swsContext = null;
        SwrContext* swrContext = null;
        AVDictionary* formatOptions = null;
        var videoStreamIndex = -1;
        var audioStreamIndex = -1;
        var outputAudioChannels = 2;
        var outputAudioSampleRate = 48000;

        try
        {
            TryInitializeNetwork();

            ConfigureRtspOptions(&formatOptions, networkTimeoutSec);
            ThrowIfError(ffmpeg.avformat_open_input(&formatContext, rtspUrl, null, &formatOptions), T("ExceptionPlayerOpenRtspFailed"));
            ffmpeg.av_dict_free(&formatOptions);

            ThrowIfError(ffmpeg.avformat_find_stream_info(formatContext, null), T("ExceptionPlayerReadStreamInfoFailed"));

            videoStreamIndex = ffmpeg.av_find_best_stream(formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (videoStreamIndex < 0)
            {
                throw new InvalidOperationException(T("ExceptionPlayerNoVideoStream"));
            }

            var videoStream = formatContext->streams[videoStreamIndex];
            var videoCodec = ffmpeg.avcodec_find_decoder(videoStream->codecpar->codec_id);
            if (videoCodec is null)
            {
                throw new InvalidOperationException(T("ExceptionPlayerNoDecoder"));
            }

            videoCodecContext = ffmpeg.avcodec_alloc_context3(videoCodec);
            if (videoCodecContext is null)
            {
                throw new InvalidOperationException(T("ExceptionPlayerAllocateCodecContextFailed"));
            }

            ThrowIfError(ffmpeg.avcodec_parameters_to_context(videoCodecContext, videoStream->codecpar), T("ExceptionPlayerCopyCodecParametersFailed"));
            ThrowIfError(ffmpeg.avcodec_open2(videoCodecContext, videoCodec, null), T("ExceptionPlayerOpenCodecFailed"));

            if (soundEnabled)
            {
                audioStreamIndex = ffmpeg.av_find_best_stream(formatContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
                if (audioStreamIndex >= 0)
                {
                    var audioStream = formatContext->streams[audioStreamIndex];
                    var audioCodec = ffmpeg.avcodec_find_decoder(audioStream->codecpar->codec_id);
                    if (audioCodec is not null)
                    {
                        audioCodecContext = ffmpeg.avcodec_alloc_context3(audioCodec);
                        if (audioCodecContext is not null &&
                            ffmpeg.avcodec_parameters_to_context(audioCodecContext, audioStream->codecpar) >= 0 &&
                            ffmpeg.avcodec_open2(audioCodecContext, audioCodec, null) >= 0)
                        {
                            var inputChannels = audioCodecContext->ch_layout.nb_channels > 0
                                ? audioCodecContext->ch_layout.nb_channels
                                : 2;
                            outputAudioChannels = inputChannels <= 1 ? 1 : 2;
                            outputAudioSampleRate = audioCodecContext->sample_rate > 0 ? audioCodecContext->sample_rate : outputAudioSampleRate;
                            AVChannelLayout outputLayout = default;
                            AVChannelLayout defaultInputLayout = default;
                            ffmpeg.av_channel_layout_default(&outputLayout, outputAudioChannels);

                            AVChannelLayout* inputLayoutPtr = &audioCodecContext->ch_layout;
                            if (inputLayoutPtr->nb_channels <= 0)
                            {
                                ffmpeg.av_channel_layout_default(&defaultInputLayout, inputChannels);
                                inputLayoutPtr = &defaultInputLayout;
                            }

                            swrContext = ffmpeg.swr_alloc();

                            var swrConfigured = swrContext is not null &&
                                ffmpeg.av_opt_set_chlayout(swrContext, "in_chlayout", inputLayoutPtr, 0) >= 0 &&
                                ffmpeg.av_opt_set_chlayout(swrContext, "out_chlayout", &outputLayout, 0) >= 0 &&
                                ffmpeg.av_opt_set_int(swrContext, "in_sample_rate", audioCodecContext->sample_rate, 0) >= 0 &&
                                ffmpeg.av_opt_set_int(swrContext, "out_sample_rate", outputAudioSampleRate, 0) >= 0 &&
                                ffmpeg.av_opt_set_sample_fmt(swrContext, "in_sample_fmt", audioCodecContext->sample_fmt, 0) >= 0 &&
                                ffmpeg.av_opt_set_sample_fmt(swrContext, "out_sample_fmt", AVSampleFormat.AV_SAMPLE_FMT_S16, 0) >= 0 &&
                                ffmpeg.swr_init(swrContext) >= 0;

                            ffmpeg.av_channel_layout_uninit(&outputLayout);
                            ffmpeg.av_channel_layout_uninit(&defaultInputLayout);

                            if (swrConfigured)
                            {
                                InitializeAudioOutput(outputAudioSampleRate, outputAudioChannels, audioOutputDeviceName, soundLevel / 100f);
                            }
                            else
                            {
                                if (swrContext is not null)
                                {
                                    ffmpeg.swr_free(&swrContext);
                                }

                                ffmpeg.avcodec_free_context(&audioCodecContext);
                                audioStreamIndex = -1;
                            }
                        }
                        else if (audioCodecContext is not null)
                        {
                            ffmpeg.avcodec_free_context(&audioCodecContext);
                            audioStreamIndex = -1;
                        }
                    }
                }
            }

            this._streamFps = ResolveStreamFps(videoStream, videoCodecContext);

            var width = videoCodecContext->width;
            var height = videoCodecContext->height;
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException(T("ExceptionPlayerInvalidVideoDimensions"));
            }

            var aspectRatio = width / (double)height;
            if (aspectRatio > 0)
            {
                this._streamAspectRatio = aspectRatio;
                StreamAspectRatioChanged?.Invoke(aspectRatio);
            }

            swsContext = ffmpeg.sws_getContext(
                width,
                height,
                videoCodecContext->pix_fmt,
                width,
                height,
                AVPixelFormat.AV_PIX_FMT_BGRA,
                ffmpeg.SWS_POINT,
                null,
                null,
                null);

            if (swsContext is null)
            {
                throw new InvalidOperationException(T("ExceptionPlayerInitializeScalerFailed"));
            }

            decodedVideoFrame = ffmpeg.av_frame_alloc();
            decodedAudioFrame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            if (decodedVideoFrame is null || decodedAudioFrame is null || packet is null)
            {
                throw new InvalidOperationException(T("ExceptionPlayerAllocateFramePacketFailed"));
            }

            var targetFps = maxFps > 0 ? maxFps : 0;
            var frameIntervalTicks = targetFps > 0 ? Stopwatch.Frequency / targetFps : 0;
            long nextFrameDeadline = 0;

            Volatile.Write(ref this._currentConnectionAttempt, 0);
            SetState(PlayerState.Playing, generation);

            while (!token.IsCancellationRequested)
            {
                var readResult = ffmpeg.av_read_frame(formatContext, packet);
                if (readResult == ffmpeg.AVERROR_EOF)
                {
                    break;
                }

                ThrowIfError(readResult, T("ExceptionPlayerReadPacketFailed"));

                if (packet->size > 0)
                {
                    Interlocked.Add(ref this._packetBytesCounter, packet->size);
                }

                if (packet->stream_index == videoStreamIndex)
                {
                    var sendResult = ffmpeg.avcodec_send_packet(videoCodecContext, packet);
                    if (sendResult != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        ThrowIfError(sendResult, T("ExceptionPlayerSendPacketFailed"));
                    }

                    while (!token.IsCancellationRequested)
                    {
                        var receiveResult = ffmpeg.avcodec_receive_frame(videoCodecContext, decodedVideoFrame);
                        if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
                        {
                            break;
                        }

                        ThrowIfError(receiveResult, T("ExceptionPlayerReceiveFrameFailed"));

                        if (frameIntervalTicks > 0)
                        {
                            var now = Stopwatch.GetTimestamp();
                            if (now < nextFrameDeadline)
                            {
                                continue;
                            }

                            nextFrameDeadline = now + frameIntervalTicks;
                        }

                        RenderFrame(decodedVideoFrame, swsContext, width, height, generation);
                        Interlocked.Increment(ref this._frameCounter);
                    }
                }
                else if (audioStreamIndex >= 0 && packet->stream_index == audioStreamIndex &&
                         audioCodecContext is not null && swrContext is not null && this._audioBuffer is not null)
                {
                    var sendAudioResult = ffmpeg.avcodec_send_packet(audioCodecContext, packet);
                    if (sendAudioResult != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        ThrowIfError(sendAudioResult, T("ExceptionPlayerSendPacketFailed"));
                    }

                    while (!token.IsCancellationRequested)
                    {
                        var receiveAudioResult = ffmpeg.avcodec_receive_frame(audioCodecContext, decodedAudioFrame);
                        if (receiveAudioResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveAudioResult == ffmpeg.AVERROR_EOF)
                        {
                            break;
                        }

                        ThrowIfError(receiveAudioResult, T("ExceptionPlayerReceiveFrameFailed"));
                        WriteAudioSamples(decodedAudioFrame, swrContext, outputAudioSampleRate, outputAudioChannels);
                    }
                }

                ffmpeg.av_packet_unref(packet);
            }
        }
        finally
        {
            if (formatOptions is not null)
            {
                ffmpeg.av_dict_free(&formatOptions);
            }

            if (packet is not null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (decodedVideoFrame is not null)
            {
                ffmpeg.av_frame_free(&decodedVideoFrame);
            }

            if (decodedAudioFrame is not null)
            {
                ffmpeg.av_frame_free(&decodedAudioFrame);
            }

            if (swsContext is not null)
            {
                ffmpeg.sws_freeContext(swsContext);
            }

            if (swrContext is not null)
            {
                ffmpeg.swr_free(&swrContext);
            }

            if (videoCodecContext is not null)
            {
                ffmpeg.avcodec_free_context(&videoCodecContext);
            }

            if (audioCodecContext is not null)
            {
                ffmpeg.avcodec_free_context(&audioCodecContext);
            }

            if (formatContext is not null)
            {
                ffmpeg.avformat_close_input(&formatContext);
            }

            DisposeAudioOutput();
        }
    }

    private static unsafe void ConfigureRtspOptions(AVDictionary** options, int networkTimeoutSec)
    {
        var timeoutSec = Math.Max(1L, networkTimeoutSec);
        var rtspIoTimeoutMicroseconds = checked(timeoutSec * 1_000_000L);
        var timeoutMicrosecondsText = rtspIoTimeoutMicroseconds.ToString();

        if (rtspIoTimeoutMicroseconds <= 0)
        {
            timeoutMicrosecondsText = DefaultRtspIoTimeoutMicroseconds.ToString();
        }

        ffmpeg.av_dict_set(options, "rtsp_transport", "tcp", 0);
        ffmpeg.av_dict_set(options, "buffer_size", "1048576", 0);
        ffmpeg.av_dict_set(options, "max_delay", "500000", 0);
        ffmpeg.av_dict_set(options, "stimeout", timeoutMicrosecondsText, 0);
        ffmpeg.av_dict_set(options, "rw_timeout", timeoutMicrosecondsText, 0);
        ffmpeg.av_dict_set(options, "timeout", timeoutMicrosecondsText, 0);
        ffmpeg.av_dict_set(options, "fflags", "discardcorrupt", 0);
    }

    private unsafe void WriteAudioSamples(AVFrame* decodedAudioFrame, SwrContext* swrContext, int outputSampleRate, int outputChannels)
    {
        if (this._audioBuffer is null)
        {
            return;
        }

        var inputSampleRate = decodedAudioFrame->sample_rate > 0 ? decodedAudioFrame->sample_rate : outputSampleRate;
        var delayedSamples = ffmpeg.swr_get_delay(swrContext, inputSampleRate);
        var targetSamples = (int)ffmpeg.av_rescale_rnd(
            delayedSamples + decodedAudioFrame->nb_samples,
            outputSampleRate,
            inputSampleRate,
            AVRounding.AV_ROUND_UP);

        if (targetSamples <= 0)
        {
            return;
        }

        var outputBufferSize = ffmpeg.av_samples_get_buffer_size(null, outputChannels, targetSamples, AVSampleFormat.AV_SAMPLE_FMT_S16, 1);
        if (outputBufferSize <= 0)
        {
            return;
        }

        var outputBuffer = Marshal.AllocHGlobal(outputBufferSize);
        try
        {
            var convertedData = stackalloc byte*[1];
            convertedData[0] = (byte*)outputBuffer;

            var convertedSamples = ffmpeg.swr_convert(
                swrContext,
                convertedData,
                targetSamples,
                decodedAudioFrame->extended_data,
                decodedAudioFrame->nb_samples);

            if (convertedSamples <= 0)
            {
                return;
            }

            var convertedBufferSize = ffmpeg.av_samples_get_buffer_size(null, outputChannels, convertedSamples, AVSampleFormat.AV_SAMPLE_FMT_S16, 1);
            if (convertedBufferSize <= 0)
            {
                return;
            }

            var managed = new byte[convertedBufferSize];
            Marshal.Copy(outputBuffer, managed, 0, convertedBufferSize);
            this._audioBuffer.AddSamples(managed, 0, managed.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(outputBuffer);
        }
    }

    private void InitializeAudioOutput(int sampleRate, int channels, string audioOutputDeviceName, float volume)
    {
        DisposeAudioOutput();
        this._audioBuffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, channels))
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true
        };

        this._waveOut = new WaveOutEvent
        {
            DeviceNumber = ResolveAudioOutputDeviceNumber(audioOutputDeviceName),
            DesiredLatency = 120
        };

        this._waveOut.Init(this._audioBuffer);
        this._waveOut.Volume = Math.Clamp(volume, 0f, 1f);
        this._waveOut.Play();
    }

    private void DisposeAudioOutput()
    {
        if (this._waveOut is not null)
        {
            this._waveOut.Stop();
            this._waveOut.Dispose();
            this._waveOut = null;
        }

        this._audioBuffer = null;
    }

    private int ResolveAudioOutputDeviceNumber(string audioOutputDeviceName)
    {
        if (string.IsNullOrWhiteSpace(audioOutputDeviceName))
        {
            return -1;
        }

        for (var i = 0; i < WaveOut.DeviceCount; i++)
        {
            if (string.Equals(WaveOut.GetCapabilities(i).ProductName, audioOutputDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private unsafe void RenderFrame(AVFrame* decodedFrame, SwsContext* swsContext, int width, int height, int generation)
    {
        var stride = width * 4;
        var requiredBufferSize = stride * height;
        if (this._frameBuffer is null || this._frameBuffer.Length != requiredBufferSize)
        {
            this._frameBuffer = new byte[requiredBufferSize];
        }

        fixed (byte* dstPtr = this._frameBuffer)
        {
            byte_ptrArray8 dstData = default;
            int_array8 dstLineSize = default;
            dstData[0] = dstPtr;
            dstLineSize[0] = stride;

            _ = ffmpeg.sws_scale(
                swsContext,
                decodedFrame->data,
                decodedFrame->linesize,
                0,
                height,
                dstData,
                dstLineSize);
        }

        if (Interlocked.Exchange(ref this._renderScheduled, 1) == 1)
        {
            return;
        }

        var frameSnapshot = new byte[requiredBufferSize];
        Buffer.BlockCopy(this._frameBuffer, 0, frameSnapshot, 0, requiredBufferSize);

        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (generation != Volatile.Read(ref this._playbackGeneration))
                {
                    return;
                }

                if (this._videoImage is null)
                {
                    return;
                }

                if (this._writeableBitmap is null || this._writeableBitmap.PixelWidth != width || this._writeableBitmap.PixelHeight != height)
                {
                    this._writeableBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                }

                if (!ReferenceEquals(this._videoImage.Source, this._writeableBitmap))
                {
                    this._videoImage.Source = this._writeableBitmap;
                }

                this._writeableBitmap.WritePixels(new Int32Rect(0, 0, width, height), frameSnapshot, stride, 0);
            }
            finally
            {
                Interlocked.Exchange(ref this._renderScheduled, 0);
            }
        }, DispatcherPriority.Background);
    }

    private static unsafe float? ResolveStreamFps(AVStream* videoStream, AVCodecContext* codecContext)
    {
        var avg = RationalToDouble(videoStream->avg_frame_rate);
        if (avg > 0)
        {
            return (float)avg;
        }

        var real = RationalToDouble(videoStream->r_frame_rate);
        if (real > 0)
        {
            return (float)real;
        }

        var codecFps = RationalToDouble(codecContext->framerate);
        return codecFps > 0 ? (float)codecFps : null;
    }

    private static double RationalToDouble(AVRational rational)
    {
        return rational.den == 0 ? 0d : rational.num / (double)rational.den;
    }

    private static unsafe void ThrowIfError(int errorCode, string message)
    {
        if (errorCode >= 0)
        {
            return;
        }

        var errorBuffer = stackalloc byte[1024];
        ffmpeg.av_strerror(errorCode, errorBuffer, 1024);
        var detail = Marshal.PtrToStringAnsi((nint)errorBuffer) ?? string.Format(T("ExceptionPlayerFfmpegError"), errorCode);
        throw new InvalidOperationException($"{message} {detail}");
    }

    private static void TryInitializeNetwork()
    {
        try
        {
            _ = ffmpeg.avformat_network_init();
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private void SetState(PlayerState state)
    {
        if (state is PlayerState.Stopped or PlayerState.Disconnected or PlayerState.Disconnecting)
        {
            Volatile.Write(ref this._currentConnectionAttempt, 0);
        }

        State = state;
        StateChanged?.Invoke(state);
    }

    private void SetState(PlayerState state, int generation)
    {
        if (generation != Volatile.Read(ref this._playbackGeneration))
        {
            return;
        }

        SetState(state);
    }
}
