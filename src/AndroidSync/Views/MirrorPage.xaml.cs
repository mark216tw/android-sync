using Com.AndroidSync.App.Helpers;
using Com.AndroidSync.App.Models;
using Com.AndroidSync.App.Protocol;
using Com.AndroidSync.App.Rendering;
using Com.AndroidSync.App.Services;
using Com.AndroidSync.App.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

namespace Com.AndroidSync.App.Views;

public sealed partial class MirrorPage : Page
{
    private const long PointerMoveMinIntervalMs = 4;

    private readonly IMirrorSessionService _sessionService;
    private readonly ISettingsService _settingsService;
    private readonly D3D11SwapChainRenderer _renderer;
    private readonly MainWindow _mainWindow;
    private readonly Dictionary<VirtualKey, AndroidKeyCode> _pressedKeys = [];
    private readonly HashSet<uint> _activeMultiTouchPointers = [];
    private CancellationTokenSource? _toastDismissCts;
    private bool _isPointerDown;
    private bool _isSimulatingPinch;
    private volatile bool _rendererAttached;
    private char? _pendingHighSurrogate;
    private int _lastMoveDevX = -1;
    private int _lastMoveDevY = -1;
    private long _lastMoveTickMs;

    public MirrorPage()
    {
        ViewModel = App.GetService<MirrorViewModel>();
        _sessionService = App.GetService<IMirrorSessionService>();
        _settingsService = App.GetService<ISettingsService>();
        _renderer = App.GetService<D3D11SwapChainRenderer>();
        _mainWindow = App.GetService<MainWindow>();

        InitializeComponent();
    }

    public MirrorViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _sessionService.StateChanged += OnSessionStateChanged;
        _sessionService.VideoSizeChanged += OnVideoSizeChanged;
        _sessionService.FrameDecoded += OnFrameDecoded;
        _sessionService.ErrorOccurred += OnErrorOccurred;
        _sessionService.DeviceClipboardChanged += OnDeviceClipboardChanged;
        _sessionService.PerformanceStatsUpdated += OnPerformanceStatsUpdated;
        _renderer.DeviceLost += OnRendererDeviceLost;
        Clipboard.ContentChanged += OnHostClipboardContentChanged;

        ViewModel.IsAlwaysOnTop = _mainWindow.IsAlwaysOnTop;
        ViewModel.IsCompactMode = _mainWindow.IsCompactMode;
        ViewModel.UpdateStateFromSession(_sessionService.State);
        ViewModel.UpdateResolution(_sessionService.VideoWidth, _sessionService.VideoHeight);

        if (_sessionService.State == MirrorSessionState.Connected)
        {
            EnsureRendererAttached(_sessionService.VideoWidth, _sessionService.VideoHeight);
            UpdatePanelLayout();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        _sessionService.StateChanged -= OnSessionStateChanged;
        _sessionService.VideoSizeChanged -= OnVideoSizeChanged;
        _sessionService.FrameDecoded -= OnFrameDecoded;
        _sessionService.ErrorOccurred -= OnErrorOccurred;
        _sessionService.DeviceClipboardChanged -= OnDeviceClipboardChanged;
        _sessionService.PerformanceStatsUpdated -= OnPerformanceStatsUpdated;
        _renderer.DeviceLost -= OnRendererDeviceLost;
        Clipboard.ContentChanged -= OnHostClipboardContentChanged;

        ReleaseAllPressedKeys();
        CancelPendingToastDismiss();
        _activeMultiTouchPointers.Clear();
        ResetPointerTracking();
        _pendingHighSurrogate = null;

        if (_rendererAttached)
        {
            _renderer.Detach();
            _rendererAttached = false;
        }
    }

    private void OnSessionStateChanged(object? sender, MirrorSessionState state)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.UpdateStateFromSession(state);
            if (state == MirrorSessionState.Connected)
            {
                EnsureRendererAttached(_sessionService.VideoWidth, _sessionService.VideoHeight);
                UpdatePanelLayout();
                KeyboardInputHost.Focus(FocusState.Programmatic);
            }
            else if (state == MirrorSessionState.Disconnected)
            {
                ReleaseAllPressedKeys();
                _activeMultiTouchPointers.Clear();
                ResetPointerTracking();
                _pendingHighSurrogate = null;
                if (_rendererAttached)
                {
                    _renderer.Detach();
                    _rendererAttached = false;
                }
            }
        });
    }

    private void OnVideoSizeChanged(object? sender, (int Width, int Height) size)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.UpdateResolution(size.Width, size.Height);
            EnsureRendererAttached(size.Width, size.Height);
            UpdatePanelLayout();
        });
    }

    private void OnFrameDecoded(object? sender, DecodedVideoFrame frame)
    {
        if (_rendererAttached)
        {
            _renderer.RenderFrame(frame);
        }
    }

    private void OnRendererDeviceLost(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_rendererAttached)
            {
                _renderer.RecoverDevice(MirrorSwapChainPanel);
                UpdatePanelLayout();
            }
        });
    }

    private void OnPerformanceStatsUpdated(object? sender, MirrorPerformanceStats stats)
    {
        DispatcherQueue.TryEnqueue(() => ViewModel.UpdatePerformanceStats(stats));
    }

    private void OnErrorOccurred(object? sender, string message)
    {
        DispatcherQueue.TryEnqueue(() => ViewModel.SetError(message));
    }

    private void OnDeviceClipboardChanged(object? sender, string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
            }
            catch
            {
                // Clipboard can be temporarily locked by another Windows process.
            }
        });
    }

    private async void OnHostClipboardContentChanged(object? sender, object e)
    {
        if (_sessionService.State != MirrorSessionState.Connected ||
            !_settingsService.Current.MirrorOptions.SyncClipboard)
        {
            return;
        }

        var text = await TryReadHostClipboardTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            _sessionService.SyncHostClipboardToDevice(text, paste: false);
        }
    }

    private void EnsureRendererAttached(int width, int height)
    {
        if (_rendererAttached || width <= 0 || height <= 0)
        {
            return;
        }

        _renderer.Attach(MirrorSwapChainPanel, width, height);
        _rendererAttached = true;
    }

    private void VideoContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePanelLayout();
    }

    private void MirrorSwapChainPanel_CompositionScaleChanged(SwapChainPanel sender, object args)
    {
        UpdatePanelLayout();
    }

    private void UpdatePanelLayout()
    {
        var devWidth = _sessionService.VideoWidth;
        var devHeight = _sessionService.VideoHeight;
        var availableWidth = Math.Max(VideoContainer.ActualWidth - 2, 0);
        var availableHeight = Math.Max(VideoContainer.ActualHeight - 2, 0);
        if (devWidth <= 0 || devHeight <= 0 || availableWidth <= 0 || availableHeight <= 0)
        {
            return;
        }

        var rect = CoordinateMapper.ComputeViewportRect(
            availableWidth,
            availableHeight,
            devWidth,
            devHeight);

        MirrorSwapChainPanel.Width = rect.Width;
        MirrorSwapChainPanel.Height = rect.Height;
        MirrorSwapChainPanel.Margin = new Thickness(rect.X, rect.Y, 0, 0);
        _renderer.UpdateDisplaySize(
            rect.Width,
            rect.Height,
            MirrorSwapChainPanel.CompositionScaleX,
            MirrorSwapChainPanel.CompositionScaleY);
    }

    private void VideoContainer_DragOver(object sender, DragEventArgs e)
    {
        if (_sessionService.State == MirrorSessionState.Connected
            && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "傳送至手機或安裝 APK";
            e.DragUIOverride.IsCaptionVisible = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void VideoContainer_Drop(object sender, DragEventArgs e)
    {
        if (_sessionService.State != MirrorSessionState.Connected
            || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is StorageFile file && !string.IsNullOrEmpty(file.Path))
            {
                ShowToast($"正在處理：{file.Name}…", isError: false, autoDismiss: false);
                var (success, message) = await _sessionService.PushFileOrInstallApkAsync(file.Path);
                ShowToast(message, isError: !success);
            }
        }
    }

    private async void ScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        await CaptureScreenshotAsync();
    }

    private async Task CaptureScreenshotAsync()
    {
        if (_sessionService.State != MirrorSessionState.Connected)
        {
            return;
        }

        if (!_renderer.TryCaptureCurrentFrameBgra(out var bgra, out var width, out var height))
        {
            ShowToast("目前尚無可擷取的畫面影格。", isError: true);
            return;
        }

        var (success, message) = await ScreenshotHelper.SaveAndCopyBgraAsync(bgra, width, height, copyToClipboard: true);
        ShowToast(message, isError: !success);
    }

    private void AlwaysOnTopButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleAlwaysOnTop();
    }

    private void ToggleAlwaysOnTop()
    {
        ViewModel.IsAlwaysOnTop = _mainWindow.ToggleAlwaysOnTop();
        ShowToast(ViewModel.IsAlwaysOnTop ? "已開啟視窗最上層顯示" : "已取消視窗最上層顯示", isError: false);
    }

    private void CompactModeButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsCompactMode = _mainWindow.ToggleCompactMode();
        if (ViewModel.IsCompactMode && _sessionService.VideoWidth > 0 && _sessionService.VideoHeight > 0)
        {
            _mainWindow.FitWindowToAspectRatio(_sessionService.VideoWidth, _sessionService.VideoHeight);
        }
    }

    private void FitWindowButton_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.FitWindowToAspectRatio(_sessionService.VideoWidth, _sessionService.VideoHeight);
    }

    private void StatusToastBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        CancelPendingToastDismiss();
        ViewModel.ClearStatusBanner();
    }

    private void ShowToast(string message, bool isError, bool autoDismiss = true)
    {
        CancelPendingToastDismiss();
        StatusToastBar.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        ViewModel.ShowStatusBanner(message, isError);

        if (!autoDismiss)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _toastDismissCts = cts;
        _ = AutoDismissToastAsync(isError ? 5000 : 3000, cts);
    }

    private async Task AutoDismissToastAsync(int delayMs, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(delayMs, cts.Token);
            if (!cts.IsCancellationRequested && ReferenceEquals(_toastDismissCts, cts))
            {
                ViewModel.ClearStatusBanner();
            }
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer toast or manually closed.
        }
    }

    private void CancelPendingToastDismiss()
    {
        if (_toastDismissCts is null)
        {
            return;
        }

        _toastDismissCts.Cancel();
        _toastDismissCts.Dispose();
        _toastDismissCts = null;
    }

    private void MirrorSwapChainPanel_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_sessionService.State != MirrorSessionState.Connected)
        {
            return;
        }

        KeyboardInputHost.Focus(FocusState.Pointer);

        var point = e.GetCurrentPoint(MirrorSwapChainPanel);
        if (e.Pointer.PointerDeviceType is PointerDeviceType.Touch or PointerDeviceType.Pen)
        {
            MirrorSwapChainPanel.CapturePointer(e.Pointer);
            _activeMultiTouchPointers.Add(e.Pointer.PointerId);
            if (TryMapPanelPoint(point.Position.X, point.Position.Y, out var tx, out var ty))
            {
                _sessionService.SendTouch(AndroidMotionAction.Down, tx, ty, isPrimaryPressed: true, e.Pointer.PointerId);
            }

            e.Handled = true;
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            _sessionService.SendKeyTap(AndroidKeyCode.Back);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsMiddleButtonPressed)
        {
            _sessionService.SendKeyTap(AndroidKeyCode.Home);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        MirrorSwapChainPanel.CapturePointer(e.Pointer);
        _isPointerDown = true;

        var ctrlDown = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        _isSimulatingPinch = ctrlDown;

        if (TryMapPanelPoint(point.Position.X, point.Position.Y, out var devX, out var devY))
        {
            _lastMoveDevX = devX;
            _lastMoveDevY = devY;
            _lastMoveTickMs = Environment.TickCount64;

            if (_isSimulatingPinch)
            {
                var (symX, symY) = PinchGestureCalculator.ComputeSymmetricPoint(
                    devX, devY, _sessionService.VideoWidth, _sessionService.VideoHeight);
                _sessionService.SendTouch(AndroidMotionAction.Down, devX, devY, true, PinchGestureCalculator.PrimaryPinchPointerId);
                _sessionService.SendTouch(AndroidMotionAction.Down, symX, symY, true, PinchGestureCalculator.SecondaryPinchPointerId);
            }
            else
            {
                _sessionService.SendTouch(AndroidMotionAction.Down, devX, devY, isPrimaryPressed: true);
            }
        }

        e.Handled = true;
    }

    private void MirrorSwapChainPanel_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_sessionService.State != MirrorSessionState.Connected)
        {
            return;
        }

        var point = e.GetCurrentPoint(MirrorSwapChainPanel);
        if (_activeMultiTouchPointers.Contains(e.Pointer.PointerId))
        {
            if (TryMapPanelPoint(point.Position.X, point.Position.Y, out var tx, out var ty))
            {
                _sessionService.SendTouch(AndroidMotionAction.Move, tx, ty, isPrimaryPressed: true, e.Pointer.PointerId);
            }

            e.Handled = true;
            return;
        }

        if (!_isPointerDown)
        {
            return;
        }

        if (TryMapPanelPoint(point.Position.X, point.Position.Y, out var devX, out var devY))
        {
            var nowMs = Environment.TickCount64;
            if ((devX == _lastMoveDevX && devY == _lastMoveDevY) ||
                nowMs - _lastMoveTickMs < PointerMoveMinIntervalMs)
            {
                e.Handled = true;
                return;
            }

            _lastMoveDevX = devX;
            _lastMoveDevY = devY;
            _lastMoveTickMs = nowMs;

            if (_isSimulatingPinch)
            {
                var (symX, symY) = PinchGestureCalculator.ComputeSymmetricPoint(
                    devX, devY, _sessionService.VideoWidth, _sessionService.VideoHeight);
                _sessionService.SendTouch(AndroidMotionAction.Move, devX, devY, true, PinchGestureCalculator.PrimaryPinchPointerId);
                _sessionService.SendTouch(AndroidMotionAction.Move, symX, symY, true, PinchGestureCalculator.SecondaryPinchPointerId);
            }
            else
            {
                _sessionService.SendTouch(AndroidMotionAction.Move, devX, devY, isPrimaryPressed: true);
            }
        }

        e.Handled = true;
    }

    private void MirrorSwapChainPanel_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(MirrorSwapChainPanel);
        if (_activeMultiTouchPointers.Remove(e.Pointer.PointerId))
        {
            MirrorSwapChainPanel.ReleasePointerCapture(e.Pointer);
            if (TryMapPanelPoint(point.Position.X, point.Position.Y, out var tx, out var ty))
            {
                _sessionService.SendTouch(AndroidMotionAction.Up, tx, ty, isPrimaryPressed: false, e.Pointer.PointerId);
            }

            e.Handled = true;
            return;
        }

        if (!_isPointerDown)
        {
            return;
        }

        var wasPinch = _isSimulatingPinch;
        ResetPointerTracking();
        MirrorSwapChainPanel.ReleasePointerCapture(e.Pointer);

        if (TryMapPanelPoint(point.Position.X, point.Position.Y, out var devX, out var devY))
        {
            SendPointerUp(devX, devY, wasPinch);
        }

        e.Handled = true;
    }

    private void MirrorSwapChainPanel_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(MirrorSwapChainPanel);
        MirrorSwapChainPanel.ReleasePointerCapture(e.Pointer);

        if (_activeMultiTouchPointers.Remove(e.Pointer.PointerId))
        {
            if (_sessionService.State == MirrorSessionState.Connected &&
                TryMapPanelPoint(point.Position.X, point.Position.Y, out var tx, out var ty))
            {
                _sessionService.SendTouch(AndroidMotionAction.Up, tx, ty, isPrimaryPressed: false, e.Pointer.PointerId);
            }

            return;
        }

        if (!_isPointerDown)
        {
            return;
        }

        var wasPinch = _isSimulatingPinch;
        var fallbackX = _lastMoveDevX;
        var fallbackY = _lastMoveDevY;
        ResetPointerTracking();

        if (_sessionService.State != MirrorSessionState.Connected)
        {
            return;
        }

        if (TryMapPanelPoint(point.Position.X, point.Position.Y, out var devX, out var devY))
        {
            SendPointerUp(devX, devY, wasPinch);
        }
        else if (fallbackX >= 0 && fallbackY >= 0)
        {
            SendPointerUp(fallbackX, fallbackY, wasPinch);
        }
    }

    private void SendPointerUp(int devX, int devY, bool wasPinch)
    {
        if (wasPinch)
        {
            var (symX, symY) = PinchGestureCalculator.ComputeSymmetricPoint(
                devX, devY, _sessionService.VideoWidth, _sessionService.VideoHeight);
            _sessionService.SendTouch(AndroidMotionAction.Up, devX, devY, false, PinchGestureCalculator.PrimaryPinchPointerId);
            _sessionService.SendTouch(AndroidMotionAction.Up, symX, symY, false, PinchGestureCalculator.SecondaryPinchPointerId);
        }
        else
        {
            _sessionService.SendTouch(AndroidMotionAction.Up, devX, devY, isPrimaryPressed: false);
        }
    }

    private void ResetPointerTracking()
    {
        _isPointerDown = false;
        _isSimulatingPinch = false;
        _lastMoveDevX = -1;
        _lastMoveDevY = -1;
        _lastMoveTickMs = 0;
    }

    private void MirrorSwapChainPanel_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_sessionService.State != MirrorSessionState.Connected)
        {
            return;
        }

        var point = e.GetCurrentPoint(MirrorSwapChainPanel);
        if (TryMapPanelPoint(point.Position.X, point.Position.Y, out var devX, out var devY))
        {
            var delta = point.Properties.MouseWheelDelta / 120.0f;
            var isHorizontal = point.Properties.IsHorizontalMouseWheel;
            _sessionService.SendScroll(devX, devY, isHorizontal ? delta : 0f, isHorizontal ? 0f : delta);
        }

        e.Handled = true;
    }

    private async void KeyboardInputHost_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_sessionService.State != MirrorSessionState.Connected)
        {
            return;
        }

        var metaState = GetCurrentAndroidMetaState();
        var isCtrlOnly = (metaState & AndroidMetaState.CtrlOn) != 0 &&
                         (metaState & (AndroidMetaState.AltOn | AndroidMetaState.ShiftOn | AndroidMetaState.MetaOn)) == 0;
        var isAltOnly = (metaState & AndroidMetaState.AltOn) != 0 &&
                        (metaState & (AndroidMetaState.CtrlOn | AndroidMetaState.ShiftOn | AndroidMetaState.MetaOn)) == 0;

        if (isAltOnly && await TryHandleDesktopShortcutAsync(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (isCtrlOnly && e.Key == VirtualKey.V)
        {
            e.Handled = true;
            var hostText = await TryReadHostClipboardTextAsync();
            if (!string.IsNullOrEmpty(hostText))
            {
                _sessionService.SyncHostClipboardToDevice(hostText, paste: true);
            }
            else
            {
                _sessionService.SendKey(AndroidKeyCode.V, isDown: true, repeat: 0, metaState);
                _sessionService.SendKey(AndroidKeyCode.V, isDown: false, repeat: 0, metaState);
            }

            return;
        }

        if (!KeyboardMapper.ShouldHandleAsKeyCode(e.Key, metaState) ||
            !KeyboardMapper.TryMapVirtualKey(e.Key, out var androidKey))
        {
            return;
        }

        var repeat = e.KeyStatus.WasKeyDown ? (int)Math.Max(1u, e.KeyStatus.RepeatCount) : 0;
        _pressedKeys[e.Key] = androidKey;
        _sessionService.SendKey(androidKey, isDown: true, repeat: repeat, metaState);
        e.Handled = true;
    }

    private void KeyboardInputHost_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!_pressedKeys.Remove(e.Key, out var androidKey))
        {
            return;
        }

        if (_sessionService.State == MirrorSessionState.Connected)
        {
            var metaState = GetCurrentAndroidMetaState();
            _sessionService.SendKey(androidKey, isDown: false, repeat: 0, metaState);
        }

        e.Handled = true;
    }

    private void KeyboardInputHost_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (_sessionService.State != MirrorSessionState.Connected)
        {
            return;
        }

        var ch = args.Character;
        if (ch < 0x20 || ch == 0x7F)
        {
            return;
        }

        if (char.IsHighSurrogate(ch))
        {
            _pendingHighSurrogate = ch;
            args.Handled = true;
            return;
        }

        string text;
        if (char.IsLowSurrogate(ch) && _pendingHighSurrogate is { } high)
        {
            text = new string([high, ch]);
            _pendingHighSurrogate = null;
        }
        else
        {
            _pendingHighSurrogate = null;
            text = ch.ToString();
        }

        _sessionService.SendText(text);
        args.Handled = true;
    }

    private void KeyboardInputHost_LostFocus(object sender, RoutedEventArgs e)
    {
        ReleaseAllPressedKeys();
        _pendingHighSurrogate = null;
    }

    private async Task<bool> TryHandleDesktopShortcutAsync(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.O:
                ViewModel.ToggleScreenPowerCommand.Execute(null);
                ShowToast(ViewModel.IsScreenOff ? "已關閉手機螢幕（維持同步）" : "已開啟手機螢幕", isError: false);
                return true;
            case VirtualKey.S:
                await CaptureScreenshotAsync();
                return true;
            case VirtualKey.F:
                _mainWindow.FitWindowToAspectRatio(_sessionService.VideoWidth, _sessionService.VideoHeight);
                return true;
            case VirtualKey.T:
                ToggleAlwaysOnTop();
                return true;
            case VirtualKey.M:
                ViewModel.ToggleAudioMuteCommand.Execute(null);
                ShowToast(ViewModel.IsAudioMuted ? "已靜音電腦音訊" : "已取消靜音電腦音訊", isError: false);
                return true;
            case VirtualKey.R:
                ViewModel.RotateDeviceCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }

    private void ReleaseAllPressedKeys()
    {
        if (_pressedKeys.Count == 0)
        {
            return;
        }

        if (_sessionService.State == MirrorSessionState.Connected)
        {
            foreach (var androidKey in _pressedKeys.Values)
            {
                _sessionService.SendKey(androidKey, isDown: false);
            }
        }

        _pressedKeys.Clear();
    }

    private static AndroidMetaState GetCurrentAndroidMetaState()
    {
        static bool IsDown(VirtualKey key) =>
            (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

        static bool IsLocked(VirtualKey key) =>
            (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Locked) == CoreVirtualKeyStates.Locked;

        var shift = IsDown(VirtualKey.Shift);
        var ctrl = IsDown(VirtualKey.Control);
        var alt = IsDown(VirtualKey.Menu);
        var meta = IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows);
        var caps = IsLocked(VirtualKey.CapitalLock);

        return KeyboardMapper.BuildMetaState(shift, ctrl, alt, meta, caps);
    }

    private static async Task<string?> TryReadHostClipboardTextAsync()
    {
        try
        {
            var dataPackageView = Clipboard.GetContent();
            if (dataPackageView.Contains(StandardDataFormats.Text))
            {
                return await dataPackageView.GetTextAsync();
            }
        }
        catch
        {
            // Clipboard access can transiently fail when locked by another app.
        }

        return null;
    }

    private bool TryMapPanelPoint(double x, double y, out int devX, out int devY)
    {
        var viewport = new ViewportRect(0, 0, MirrorSwapChainPanel.ActualWidth, MirrorSwapChainPanel.ActualHeight);
        return CoordinateMapper.TryMapToDevice(
            x,
            y,
            viewport,
            _sessionService.VideoWidth,
            _sessionService.VideoHeight,
            out devX,
            out devY);
    }
}
