using System.Runtime.InteropServices;
using Com.AndroidSync.App.Models;
using Com.AndroidSync.App.Services;
using Com.AndroidSync.App.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Graphics;
using WinRT.Interop;

namespace Com.AndroidSync.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly IWindowStateService _windowStateService;
    private readonly IMirrorSessionService _mirrorSessionService;
    private readonly DeviceListViewModel _deviceListViewModel;
    private readonly MirrorViewModel _mirrorViewModel;
    private bool _isClosingInProgress;
    private bool _isClosingConfirmed;

    public MainWindow(
        IWindowStateService windowStateService,
        IMirrorSessionService mirrorSessionService,
        DeviceListViewModel deviceListViewModel,
        MirrorViewModel mirrorViewModel)
    {
        _windowStateService = windowStateService;
        _mirrorSessionService = mirrorSessionService;
        _deviceListViewModel = deviceListViewModel;
        _mirrorViewModel = mirrorViewModel;

        InitializeComponent();

        ConfigureWindow();
        _deviceListViewModel.MirrorRequested += OnMirrorRequested;

        RootNavView.SelectedItem = NavDevicesItem;
        ContentFrame.Navigate(typeof(DeviceListPage));
    }

    public bool IsAlwaysOnTop =>
        AppWindow.Presenter is OverlappedPresenter presenter && presenter.IsAlwaysOnTop;

    public bool IsCompactMode { get; private set; }

    private void ConfigureWindow()
    {
        var loader = new ResourceLoader();
        Title = loader.GetString("AppTitle");

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 620;
        }

        _windowStateService.Restore(AppWindow);
        AppWindow.Closing += OnWindowClosing;
    }

    public bool ToggleAlwaysOnTop()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = !presenter.IsAlwaysOnTop;
            return presenter.IsAlwaysOnTop;
        }

        return false;
    }

    public bool ToggleCompactMode()
    {
        IsCompactMode = !IsCompactMode;
        RootNavView.IsPaneVisible = !IsCompactMode;
        RootNavView.IsPaneToggleButtonVisible = !IsCompactMode;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = IsCompactMode ? 360 : 900;
            presenter.PreferredMinimumHeight = IsCompactMode ? 480 : 620;
        }

        return IsCompactMode;
    }

    public void FitWindowToAspectRatio(int videoWidth, int videoHeight)
    {
        if (videoWidth <= 0 || videoHeight <= 0)
        {
            return;
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Maximized)
        {
            presenter.Restore();
        }

        var currentSize = AppWindow.Size;
        var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var maxWidth = displayArea?.WorkArea.Width ?? int.MaxValue;
        var maxHeight = displayArea?.WorkArea.Height ?? int.MaxValue;

        var (targetWidth, targetHeight) = ComputeFittedWindowSize(
            currentSize.Width,
            currentSize.Height,
            videoWidth,
            videoHeight,
            IsCompactMode,
            scale,
            maxWidth,
            maxHeight);

        AppWindow.Resize(new SizeInt32(targetWidth, targetHeight));
    }

    public static (int Width, int Height) ComputeFittedWindowSize(
        int currentWidth,
        int currentHeight,
        int videoWidth,
        int videoHeight,
        bool isCompactMode,
        double rasterizationScale,
        int maxWorkWidth,
        int maxWorkHeight)
    {
        var scale = rasterizationScale > 0 ? rasterizationScale : 1.0;
        var chromeWidth = (int)Math.Round((isCompactMode ? 70 : 122) * scale);
        var chromeHeight = (int)Math.Round(96 * scale);
        var minContentHeight = (int)Math.Round(360 * scale);
        var minWidth = (int)Math.Round((isCompactMode ? 360 : 900) * scale);
        var minHeight = (int)Math.Round((isCompactMode ? 480 : 620) * scale);

        var contentHeight = Math.Max(currentHeight - chromeHeight, minContentHeight);
        var aspect = (double)videoWidth / videoHeight;
        var targetWidth = (int)Math.Round((contentHeight * aspect) + chromeWidth);
        var targetHeight = currentHeight;

        var upperWidth = Math.Max(minWidth, maxWorkWidth);
        var upperHeight = Math.Max(minHeight, maxWorkHeight);
        targetWidth = Math.Clamp(targetWidth, Math.Min(minWidth, maxWorkWidth), upperWidth);
        targetHeight = Math.Clamp(targetHeight, Math.Min(minHeight, maxWorkHeight), upperHeight);

        return (targetWidth, targetHeight);
    }

    private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isClosingConfirmed)
        {
            return;
        }

        args.Cancel = true;
        if (_isClosingInProgress)
        {
            return;
        }

        _isClosingInProgress = true;
        try
        {
            await _windowStateService.SaveAsync(sender);
            await _mirrorSessionService.StopAsync();
        }
        finally
        {
            _isClosingConfirmed = true;
            Close();
        }
    }

    private async void OnMirrorRequested(object? sender, AndroidDevice device)
    {
        RootNavView.SelectedItem = NavMirrorItem;
        ContentFrame.Navigate(typeof(MirrorPage));
        await _mirrorViewModel.StartSessionAsync(device);
    }

    private void RootNavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        var targetPage = tag switch
        {
            "devices" => typeof(DeviceListPage),
            "mirror" => typeof(MirrorPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DeviceListPage)
        };

        if (ContentFrame.CurrentSourcePageType != targetPage)
        {
            ContentFrame.Navigate(targetPage);
        }
    }

    public void BringToForeground()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        var hwnd = WindowNative.GetWindowHandle(this);
        if (hwnd != nint.Zero)
        {
            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, SwRestore);
            }

            SetForegroundWindow(hwnd);
        }

        AppWindow.Show();
        Activate();
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);
}
