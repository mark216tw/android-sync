using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Com.AndroidSync.App.Audio;
using Com.AndroidSync.App.Helpers;
using Com.AndroidSync.App.Models;
using Com.AndroidSync.App.Protocol;
using Com.AndroidSync.App.Rendering;
using Microsoft.Extensions.Logging;

namespace Com.AndroidSync.App.Services;

public sealed class MirrorSessionService : IMirrorSessionService
{
    private const string RemoteServerPath = "/data/local/tmp/scrcpy-server.jar";
    private static readonly int[] ReconnectBackoffMs = [1500, 3000, 5000];

    private readonly IAdbService _adbService;
    private readonly ISettingsService _settingsService;
    private readonly D3D11SwapChainRenderer _renderer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MirrorSessionService> _logger;

    private ScrcpyProtocolClient? _protocolClient;
    private MediaFoundationH264Decoder? _decoder;
    private MediaFoundationAacDecoder? _aacDecoder;
    private IAudioPlayer? _audioPlayer;
    private Process? _serverProcess;
    private readonly Lock _serverLogLock = new();
    private readonly Queue<string> _serverLogLines = new();
    private readonly Lock _pushedServerLock = new();
    private readonly HashSet<(string Serial, long FileLength, long LastWriteTicks)> _pushedServerCache = [];
    private readonly ClipboardSyncCoordinator _clipboardCoordinator = new();
    private CancellationTokenSource? _sessionCts;
    private Task? _videoReaderTask;
    private Task? _videoDecodeTask;
    private Task? _audioLoopTask;
    private Task? _reconnectTask;
    private int _forwardedPort;
    private string? _forwardedSerial;
    private byte[]? _cachedSpsPps;
    private byte[] _keyFrameScratch = [];
    private long _clipboardSequence;
    private volatile bool _stopRequested;
    private string _activeVideoCodecName = "H.264";
    private string _activeAudioMode = "關閉";

    public MirrorSessionService(
        IAdbService adbService,
        ISettingsService settingsService,
        D3D11SwapChainRenderer renderer,
        ILoggerFactory loggerFactory,
        ILogger<MirrorSessionService> logger)
    {
        _adbService = adbService;
        _settingsService = settingsService;
        _renderer = renderer;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _renderer.DeviceLost += OnRendererDeviceLost;
    }

    public MirrorSessionState State { get; private set; } = MirrorSessionState.Disconnected;

    public AndroidDevice? ActiveDevice { get; private set; }

    public int VideoWidth { get; private set; }

    public int VideoHeight { get; private set; }

    public bool IsScreenOff { get; private set; }

    public bool IsAudioActive { get; private set; }

    public bool IsAudioMuted { get; private set; }

    public bool IsGpuZeroCopyActive { get; private set; }

    public event EventHandler<MirrorSessionState>? StateChanged;

    public event EventHandler<(int Width, int Height)>? VideoSizeChanged;

    public event EventHandler<DecodedVideoFrame>? FrameDecoded;

    public event EventHandler<string>? ErrorOccurred;

    public event EventHandler<string>? DeviceClipboardChanged;

    public event EventHandler<MirrorPerformanceStats>? PerformanceStatsUpdated;

    public async Task StartAsync(AndroidDevice device, CancellationToken cancellationToken = default)
    {
        await StopAsync().ConfigureAwait(false);

        _stopRequested = false;
        ActiveDevice = device;
        IsScreenOff = false;
        SetState(MirrorSessionState.Connecting);

        try
        {
            await ConnectInternalAsync(device, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "啟動畫面同步失敗");
            SetState(MirrorSessionState.Error);
            ErrorOccurred?.Invoke(this, ex.Message);
            await CleanupResourcesAsync().ConfigureAwait(false);
        }
    }

    private async Task ConnectInternalAsync(AndroidDevice device, CancellationToken cancellationToken)
    {
        var serverPath = _adbService.ResolveScrcpyServerPath();
        if (string.IsNullOrEmpty(serverPath))
        {
            throw new FileNotFoundException(
                "找不到 scrcpy-server，請將 scrcpy-server 放至 Tools 資料夾或於「設定」頁指定檔案路徑。");
        }

        var skippedPush = await EnsureScrcpyServerPushedAsync(device.Serial, serverPath, cancellationToken)
            .ConfigureAwait(false);

        var scid = Random.Shared.Next(1, int.MaxValue);
        var scidHex = scid.ToString("x8");
        var socketName = $"scrcpy_{scidHex}";
        var localPort = GetAvailableTcpPort();

        var forwardResult = await _adbService.ForwardPortAsync(device.Serial, localPort, socketName, cancellationToken)
            .ConfigureAwait(false);
        if (!forwardResult.IsSuccess)
        {
            throw new InvalidOperationException($"建立連接埠轉發失敗：{forwardResult.StandardError}");
        }

        _forwardedPort = localPort;
        _forwardedSerial = device.Serial;

        var options = _settingsService.Current.MirrorOptions;
        var selectedVideoCodec = options.VideoCodec;
        if (selectedVideoCodec == VideoCodecPreference.H265 && !MediaFoundationH264Decoder.IsHevcDecoderAvailable())
        {
            _logger.LogWarning("系統未安裝 Windows HEVC 解碼器，將自動退回 H.264。");
            selectedVideoCodec = VideoCodecPreference.H264;
        }

        var videoCodecArg = selectedVideoCodec == VideoCodecPreference.H265 ? "h265" : "h264";
        _activeVideoCodecName = selectedVideoCodec == VideoCodecPreference.H265 ? "H.265" : "H.264";

        var audioCodecArg = options.UseCompressedAudio ? "aac" : "raw";
        var audioArgs = options.EnableAudio ? $"audio=true audio_codec={audioCodecArg} " : "audio=false ";
        var shellArgs =
            $"CLASSPATH={RemoteServerPath} app_process / com.genymobile.scrcpy.Server " +
            $"{options.ScrcpyServerVersion} scid={scidHex} log_level=info " +
            $"video=true video_codec={videoCodecArg} {audioArgs}control=true tunnel_forward=true " +
            $"send_device_meta=true send_frame_meta=true " +
            $"clipboard_autosync={options.SyncClipboard.ToString().ToLowerInvariant()} " +
            $"max_size={options.MaxSize} video_bit_rate={options.BitRateBps} max_fps={options.MaxFps} " +
            $"stay_awake={options.StayAwake.ToString().ToLowerInvariant()} power_off_on_close=false";

        ClearServerLogs();
        _clipboardCoordinator.Reset();
        IsAudioActive = false;
        IsAudioMuted = false;
        _activeAudioMode = "關閉";
        _serverProcess = _adbService.StartDeviceShellProcess(device.Serial, shellArgs);
        AttachServerOutputReaders(_serverProcess);
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _protocolClient = new ScrcpyProtocolClient(_loggerFactory.CreateLogger<ScrcpyProtocolClient>());
        _protocolClient.DeviceClipboardReceived += OnProtocolDeviceClipboardReceived;
        ScrcpyDeviceMetadata metadata;
        try
        {
            metadata = await _protocolClient
                .ConnectAsync(localPort, _sessionCts.Token, GetServerExitError, enableAudio: options.EnableAudio)
                .ConfigureAwait(false);
        }
        catch
        {
            if (skippedPush)
            {
                InvalidatePushedServerCache(device.Serial);
            }

            throw;
        }

        VideoWidth = metadata.Width;
        VideoHeight = metadata.Height;
        VideoSizeChanged?.Invoke(this, (VideoWidth, VideoHeight));

        var dxgiDeviceManager = _renderer.GetDxgiDeviceManager();
        _decoder = new MediaFoundationH264Decoder(_loggerFactory.CreateLogger<MediaFoundationH264Decoder>());
        _decoder.Initialize(VideoWidth, VideoHeight, dxgiDeviceManager, selectedVideoCodec);
        IsGpuZeroCopyActive = _decoder.IsHardwareAccelerated && _renderer.IsGpuZeroCopySupported;

        InitializeAudioPipeline(_sessionCts.Token);

        if (options.TurnScreenOffOnStart)
        {
            IsScreenOff = true;
            _protocolClient.EnqueueControlMessage(ScrcpyControlSerializer.CreateSetScreenPowerModeMessage(false));
        }

        SetState(MirrorSessionState.Connected);

        var videoQueue = Channel.CreateBounded<PooledVideoFrame>(new BoundedChannelOptions(4)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        var token = _sessionCts.Token;
        _videoReaderTask = Task.Run(() => VideoReadLoopAsync(videoQueue.Writer, token), token);
        _videoDecodeTask = Task.Run(() => VideoDecodeLoopAsync(videoQueue.Reader, token), token);
    }

    private async Task<bool> EnsureScrcpyServerPushedAsync(string serial, string serverPath, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(serverPath);
        var cacheKey = (serial, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks);
        lock (_pushedServerLock)
        {
            if (_pushedServerCache.Contains(cacheKey))
            {
                _logger.LogDebug("已快取 scrcpy-server 推送紀錄，略過重複推送 ({Serial})", serial);
                return true;
            }
        }

        var pushResult = await _adbService.PushFileAsync(serial, serverPath, RemoteServerPath, cancellationToken)
            .ConfigureAwait(false);
        if (!pushResult.IsSuccess)
        {
            throw new InvalidOperationException($"推送 scrcpy-server 至手機失敗：{pushResult.StandardError}");
        }

        lock (_pushedServerLock)
        {
            _pushedServerCache.RemoveWhere(k => string.Equals(k.Serial, serial, StringComparison.Ordinal));
            _pushedServerCache.Add(cacheKey);
        }

        return false;
    }

    private void InvalidatePushedServerCache(string serial)
    {
        lock (_pushedServerLock)
        {
            _pushedServerCache.RemoveWhere(k => string.Equals(k.Serial, serial, StringComparison.Ordinal));
        }
    }

    private void InitializeAudioPipeline(CancellationToken token)
    {
        if (_protocolClient is null || !_protocolClient.IsAudioConnected)
        {
            return;
        }

        var codecId = _protocolClient.AudioCodecId;
        if (codecId is not (ScrcpyProtocolClient.AudioCodecRaw or ScrcpyProtocolClient.AudioCodecAac))
        {
            _logger.LogWarning("不支援的音訊編碼 0x{CodecId:X8}，將以純畫面模式繼續", codecId);
            return;
        }

        try
        {
            _audioPlayer = CreateAudioPlayer();
            IsAudioActive = true;
            var codecLabel = codecId == ScrcpyProtocolClient.AudioCodecAac ? "AAC" : "RAW";
            var playerLabel = _audioPlayer is WasapiAudioPlayer ? "WASAPI" : "WaveOut";
            _activeAudioMode = $"{codecLabel}/{playerLabel}";
            _audioLoopTask = Task.Run(() => AudioPlaybackLoopAsync(codecId, token), token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "初始化音訊輸出失敗，將以純畫面模式繼續");
            _audioPlayer?.Dispose();
            _audioPlayer = null;
            IsAudioActive = false;
            _activeAudioMode = "關閉";
        }
    }

    private IAudioPlayer CreateAudioPlayer()
    {
        try
        {
            var wasapi = new WasapiAudioPlayer(_loggerFactory.CreateLogger<WasapiAudioPlayer>());
            wasapi.Initialize(sampleRate: 48_000, channels: 2, bitsPerSample: 16);
            return wasapi;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WASAPI 初始化失敗，退回 WaveOutAudioPlayer");
            var waveOut = new WaveOutAudioPlayer(_loggerFactory.CreateLogger<WaveOutAudioPlayer>());
            waveOut.Initialize(sampleRate: 48_000, channels: 2, bitsPerSample: 16);
            return waveOut;
        }
    }

    public async Task StopAsync()
    {
        _stopRequested = true;
        await CleanupResourcesAsync().ConfigureAwait(false);
        ActiveDevice = null;
        IsScreenOff = false;
        SetState(MirrorSessionState.Disconnected);
    }

    public void SendTouch(
        AndroidMotionAction action,
        int x,
        int y,
        bool isPrimaryPressed,
        ulong pointerId = unchecked((ulong)-1L))
    {
        if (_protocolClient is null || VideoWidth <= 0 || VideoHeight <= 0)
        {
            return;
        }

        var packet = ScrcpyControlSerializer.CreateTouchEventMessage(
            action,
            x,
            y,
            (ushort)VideoWidth,
            (ushort)VideoHeight,
            isPrimaryPressed,
            pointerId);
        _protocolClient.EnqueueControlMessage(packet);
    }

    public void SendScroll(int x, int y, float horizontalScroll, float verticalScroll)
    {
        if (_protocolClient is null || VideoWidth <= 0 || VideoHeight <= 0)
        {
            return;
        }

        var packet = ScrcpyControlSerializer.CreateScrollEventMessage(
            x,
            y,
            (ushort)VideoWidth,
            (ushort)VideoHeight,
            horizontalScroll,
            verticalScroll);
        _protocolClient.EnqueueControlMessage(packet);
    }

    public void SendKeyTap(AndroidKeyCode keyCode)
    {
        if (_protocolClient is null)
        {
            return;
        }

        _protocolClient.EnqueueControlMessage(ScrcpyControlSerializer.CreateKeyCodeMessage(keyCode, isDown: true));
        _protocolClient.EnqueueControlMessage(ScrcpyControlSerializer.CreateKeyCodeMessage(keyCode, isDown: false));
    }

    public void SendKey(AndroidKeyCode keyCode, bool isDown, int repeat = 0, AndroidMetaState metaState = AndroidMetaState.None)
    {
        if (_protocolClient is null)
        {
            return;
        }

        _protocolClient.EnqueueControlMessage(
            ScrcpyControlSerializer.CreateKeyCodeMessage(keyCode, isDown, repeat, metaState));
    }

    public void SendText(string text)
    {
        if (_protocolClient is null || string.IsNullOrEmpty(text))
        {
            return;
        }

        if (KeyboardMapper.IsAsciiPrintable(text))
        {
            _protocolClient.EnqueueControlMessage(ScrcpyControlSerializer.CreateInjectTextMessage(text));
            return;
        }

        _clipboardCoordinator.RecordImeInjectedText(text);
        var sequence = (ulong)Interlocked.Increment(ref _clipboardSequence);
        _protocolClient.EnqueueControlMessage(
            ScrcpyControlSerializer.CreateSetClipboardMessage(text, paste: true, sequence: sequence));
    }

    public void SyncHostClipboardToDevice(string text, bool paste = false)
    {
        if (_protocolClient is null || string.IsNullOrEmpty(text))
        {
            return;
        }

        if (!paste && !_settingsService.Current.MirrorOptions.SyncClipboard)
        {
            return;
        }

        if (!paste && !_clipboardCoordinator.ShouldSendToDevice(text))
        {
            return;
        }

        if (paste)
        {
            _clipboardCoordinator.ShouldSendToDevice(text);
        }

        var sequence = (ulong)Interlocked.Increment(ref _clipboardSequence);
        _protocolClient.EnqueueControlMessage(
            ScrcpyControlSerializer.CreateSetClipboardMessage(text, paste: paste, sequence: sequence));
    }

    public void RequestDeviceClipboard(ScrcpyClipboardCopyKey copyKey = ScrcpyClipboardCopyKey.None)
    {
        _protocolClient?.EnqueueControlMessage(ScrcpyControlSerializer.CreateGetClipboardMessage(copyKey));
    }

    private void OnProtocolDeviceClipboardReceived(object? sender, string text)
    {
        if (!_settingsService.Current.MirrorOptions.SyncClipboard)
        {
            return;
        }

        if (_clipboardCoordinator.ShouldApplyFromDevice(text))
        {
            DeviceClipboardChanged?.Invoke(this, text);
        }
    }

    public void ToggleScreenPower()
    {
        if (_protocolClient is null)
        {
            return;
        }

        IsScreenOff = !IsScreenOff;
        _protocolClient.EnqueueControlMessage(ScrcpyControlSerializer.CreateSetScreenPowerModeMessage(!IsScreenOff));
    }

    public void ToggleAudioMute()
    {
        IsAudioMuted = !IsAudioMuted;
        if (_audioPlayer is not null)
        {
            _audioPlayer.IsMuted = IsAudioMuted;
        }
    }

    public void RequestRotate()
    {
        _protocolClient?.EnqueueControlMessage(ScrcpyControlSerializer.CreateRotateDeviceMessage());
    }

    public async Task<(bool Success, string Message)> PushFileOrInstallApkAsync(
        string localFilePath,
        CancellationToken cancellationToken = default)
    {
        var device = ActiveDevice;
        if (device is null || State != MirrorSessionState.Connected)
        {
            return (false, "目前未連線至任何裝置。");
        }

        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
        {
            return (false, "找不到要傳輸的檔案。");
        }

        var fileName = Path.GetFileName(localFilePath);
        if (localFilePath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
        {
            var install = await _adbService.InstallApkAsync(device.Serial, localFilePath, cancellationToken)
                .ConfigureAwait(false);
            return install.IsSuccess
                ? (true, $"已安裝 APK：{fileName}")
                : (false, $"安裝 APK 失敗（{fileName}）：{install.StandardError}");
        }

        var remotePath = $"/sdcard/Download/{fileName}";
        var push = await _adbService.PushFileAsync(device.Serial, localFilePath, remotePath, cancellationToken)
            .ConfigureAwait(false);
        if (!push.IsSuccess)
        {
            return (false, $"傳送檔案失敗（{fileName}）：{push.StandardError}");
        }

        _ = await _adbService.ScanMediaFileAsync(device.Serial, remotePath, cancellationToken).ConfigureAwait(false);
        return (true, $"已傳送至手機下載資料夾：{fileName}");
    }

    private async Task VideoReadLoopAsync(ChannelWriter<PooledVideoFrame> writer, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _protocolClient is not null)
            {
                var packet = await _protocolClient.ReadNextVideoFramePooledAsync(cancellationToken).ConfigureAwait(false);
                if (packet is null)
                {
                    break;
                }

                await writer.WriteAsync(packet.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "視訊網路讀取迴圈中斷");
            writer.TryComplete(ex);
            return;
        }

        writer.TryComplete();
    }

    private async Task VideoDecodeLoopAsync(ChannelReader<PooledVideoFrame> reader, CancellationToken cancellationToken)
    {
        var statsWatch = Stopwatch.StartNew();
        var frameCount = 0;
        long totalBytes = 0;
        long totalDecodeTicks = 0;
        Exception? fatalError = null;

        try
        {
            await foreach (var frame in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (_decoder is null)
                    {
                        break;
                    }

                    totalBytes += frame.Length;
                    if (frame.IsConfig)
                    {
                        _cachedSpsPps = frame.Span.ToArray();
                        continue;
                    }

                    ReadOnlySpan<byte> inputSpan;
                    if (frame.IsKeyFrame && _cachedSpsPps is not null)
                    {
                        var needed = _cachedSpsPps.Length + frame.Length;
                        if (_keyFrameScratch.Length < needed)
                        {
                            _keyFrameScratch = new byte[needed];
                        }

                        Buffer.BlockCopy(_cachedSpsPps, 0, _keyFrameScratch, 0, _cachedSpsPps.Length);
                        Buffer.BlockCopy(frame.Buffer, 0, _keyFrameScratch, _cachedSpsPps.Length, frame.Length);
                        inputSpan = _keyFrameScratch.AsSpan(0, needed);
                    }
                    else
                    {
                        inputSpan = frame.Span;
                    }

                    var t0 = Stopwatch.GetTimestamp();
                    var delivered = _decoder.DecodeFrames(inputSpan, frame.PtsMicroseconds, decoded =>
                    {
                        if (decoded.Width != VideoWidth || decoded.Height != VideoHeight)
                        {
                            VideoWidth = decoded.Width;
                            VideoHeight = decoded.Height;
                            VideoSizeChanged?.Invoke(this, (VideoWidth, VideoHeight));
                        }

                        if (reader.Count == 0)
                        {
                            FrameDecoded?.Invoke(this, decoded);
                        }
                    });

                    if (delivered > 0)
                    {
                        frameCount += delivered;
                        totalDecodeTicks += Stopwatch.GetTimestamp() - t0;
                    }

                    if (statsWatch.ElapsedMilliseconds >= 1000)
                    {
                        var seconds = statsWatch.Elapsed.TotalSeconds;
                        var fps = frameCount / seconds;
                        var avgDecodeMs = frameCount > 0
                            ? (totalDecodeTicks * 1000.0 / Stopwatch.Frequency) / frameCount
                            : 0.0;
                        var bitrateMbps = (totalBytes * 8.0) / (seconds * 1_000_000.0);

                        PerformanceStatsUpdated?.Invoke(
                            this,
                            new MirrorPerformanceStats(
                                fps,
                                avgDecodeMs,
                                bitrateMbps,
                                _activeVideoCodecName,
                                IsGpuZeroCopyActive,
                                _activeAudioMode));

                        statsWatch.Restart();
                        frameCount = 0;
                        totalBytes = 0;
                        totalDecodeTicks = 0;
                    }
                }
                finally
                {
                    frame.Return();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
            return;
        }
        catch (Exception ex)
        {
            fatalError = ex;
            _logger.LogError(ex, "視訊解碼迴圈發生錯誤");
        }
        finally
        {
            while (reader.TryRead(out var leftover))
            {
                leftover.Return();
            }
        }

        if (_stopRequested)
        {
            return;
        }

        var device = ActiveDevice;
        if (device is not null && _settingsService.Current.MirrorOptions.AutoReconnect)
        {
            TriggerAutoReconnect(device, fatalError?.Message);
        }
        else if (fatalError is not null)
        {
            SetState(MirrorSessionState.Error);
            ErrorOccurred?.Invoke(this, $"視訊串流中斷：{fatalError.Message}");
        }
        else if (State == MirrorSessionState.Connected)
        {
            SetState(MirrorSessionState.Disconnected);
        }
    }

    private void OnRendererDeviceLost(object? sender, EventArgs e)
    {
        if (_stopRequested || ActiveDevice is null || State != MirrorSessionState.Connected)
        {
            return;
        }

        _logger.LogWarning("收到 D3D11 DeviceLost 通知，觸發串流重建...");
        TriggerAutoReconnect(ActiveDevice, "Direct3D 11 裝置重設");
    }

    private void TriggerAutoReconnect(AndroidDevice device, string? reason)
    {
        if (_reconnectTask is not null && !_reconnectTask.IsCompleted)
        {
            return;
        }

        _reconnectTask = Task.Run(async () =>
        {
            SetState(MirrorSessionState.Reconnecting);
            _logger.LogWarning("串流中斷（{Reason}），啟動自動重連...", reason ?? "連線關閉");

            for (var attempt = 0; attempt < ReconnectBackoffMs.Length; attempt++)
            {
                if (_stopRequested)
                {
                    return;
                }

                await CleanupResourcesAsync().ConfigureAwait(false);
                await Task.Delay(ReconnectBackoffMs[attempt]).ConfigureAwait(false);

                if (_stopRequested)
                {
                    return;
                }

                try
                {
                    await ConnectInternalAsync(device, CancellationToken.None).ConfigureAwait(false);
                    _logger.LogInformation("自動重連成功（第 {Attempt} 次嘗試）", attempt + 1);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "自動重連第 {Attempt} 次失敗", attempt + 1);
                }
            }

            if (!_stopRequested)
            {
                await CleanupResourcesAsync().ConfigureAwait(false);
                SetState(MirrorSessionState.Error);
                ErrorOccurred?.Invoke(this, $"串流中斷且自動重連失敗（{reason ?? "裝置離線"}）。");
            }
        });
    }

    private async Task AudioPlaybackLoopAsync(uint audioCodecId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _protocolClient is not null && _audioPlayer is not null)
            {
                var packet = await _protocolClient.ReadNextAudioPacketViewAsync(cancellationToken).ConfigureAwait(false);
                if (packet is null)
                {
                    break;
                }

                var audio = packet.Value;
                if (audioCodecId == ScrcpyProtocolClient.AudioCodecRaw)
                {
                    if (!audio.IsConfig && audio.Data.Length > 0 && !IsAudioMuted)
                    {
                        _audioPlayer.WritePcm(audio.Data.Span);
                    }

                    continue;
                }

                if (audioCodecId == ScrcpyProtocolClient.AudioCodecAac)
                {
                    if (audio.IsConfig)
                    {
                        _aacDecoder?.Dispose();
                        _aacDecoder = new MediaFoundationAacDecoder(_loggerFactory.CreateLogger<MediaFoundationAacDecoder>());
                        _aacDecoder.Initialize(audio.Data.Span, sampleRate: 48_000, channels: 2);
                        continue;
                    }

                    if (audio.Data.Length == 0)
                    {
                        continue;
                    }

                    if (_aacDecoder is null)
                    {
                        _aacDecoder = new MediaFoundationAacDecoder(_loggerFactory.CreateLogger<MediaFoundationAacDecoder>());
                        _aacDecoder.Initialize([], sampleRate: 48_000, channels: 2);
                    }

                    var player = _audioPlayer;
                    _aacDecoder.Decode(audio.Data.Span, audio.PtsMicroseconds, player.WritePcm);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "音訊串流讀取中斷，將維持畫面同步");
        }
        finally
        {
            IsAudioActive = false;
        }
    }

    private async Task CleanupResourcesAsync()
    {
        if (_sessionCts is not null)
        {
            await _sessionCts.CancelAsync().ConfigureAwait(false);
        }

        if (_protocolClient is not null)
        {
            _protocolClient.DeviceClipboardReceived -= OnProtocolDeviceClipboardReceived;
            await _protocolClient.DisposeAsync().ConfigureAwait(false);
            _protocolClient = null;
        }

        await AwaitTaskQuietlyAsync(_videoReaderTask, "視訊讀取迴圈").ConfigureAwait(false);
        _videoReaderTask = null;

        await AwaitTaskQuietlyAsync(_videoDecodeTask, "視訊解碼迴圈").ConfigureAwait(false);
        _videoDecodeTask = null;

        await AwaitTaskQuietlyAsync(_audioLoopTask, "音訊播放迴圈").ConfigureAwait(false);
        _audioLoopTask = null;

        _aacDecoder?.Dispose();
        _aacDecoder = null;

        _audioPlayer?.Dispose();
        _audioPlayer = null;
        IsAudioActive = false;
        IsAudioMuted = false;
        _activeAudioMode = "關閉";

        _decoder?.Dispose();
        _decoder = null;
        _cachedSpsPps = null;
        IsGpuZeroCopyActive = false;

        if (_serverProcess is not null)
        {
            try
            {
                if (!_serverProcess.HasExited)
                {
                    _serverProcess.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "結束 scrcpy-server 行程時忽略例外");
            }
            finally
            {
                _serverProcess.Dispose();
                _serverProcess = null;
            }
        }

        if (_forwardedPort > 0 && !string.IsNullOrEmpty(_forwardedSerial))
        {
            try
            {
                await _adbService.RemoveForwardAsync(_forwardedSerial, _forwardedPort).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "移除 ADB forward 失敗");
            }

            _forwardedPort = 0;
            _forwardedSerial = null;
        }

        _sessionCts?.Dispose();
        _sessionCts = null;
        ClearServerLogs();
    }

    private async Task AwaitTaskQuietlyAsync(Task? task, string name)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "結束{Name}時忽略例外", name);
        }
    }

    private void AttachServerOutputReaders(Process process)
    {
        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            RecordServerLog(e.Data);
            _logger.LogInformation("[scrcpy-server] {Line}", e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            RecordServerLog(e.Data);
            _logger.LogWarning("[scrcpy-server stderr] {Line}", e.Data);
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private void RecordServerLog(string line)
    {
        lock (_serverLogLock)
        {
            _serverLogLines.Enqueue(line);
            while (_serverLogLines.Count > 20)
            {
                _serverLogLines.Dequeue();
            }
        }
    }

    private void ClearServerLogs()
    {
        lock (_serverLogLock)
        {
            _serverLogLines.Clear();
        }
    }

    private string? GetServerExitError()
    {
        var process = _serverProcess;
        if (process is null || !process.HasExited)
        {
            return null;
        }

        Thread.Sleep(50);

        string summary;
        lock (_serverLogLock)
        {
            summary = string.Join(" | ", _serverLogLines);
        }

        return string.IsNullOrWhiteSpace(summary)
            ? $"結束代碼 {process.ExitCode}"
            : summary;
    }

    private void SetState(MirrorSessionState newState)
    {
        State = newState;
        StateChanged?.Invoke(this, newState);
    }

    private static int GetAvailableTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        _stopRequested = true;
        _renderer.DeviceLost -= OnRendererDeviceLost;
        await CleanupResourcesAsync().ConfigureAwait(false);
    }
}
