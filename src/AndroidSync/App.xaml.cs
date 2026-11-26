using Com.AndroidSync.App.Rendering;
using Com.AndroidSync.App.Services;
using Com.AndroidSync.App.ViewModels;
using Com.AndroidSync.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace Com.AndroidSync.App;

public partial class App : Application
{
    private readonly IHost _host;
    private readonly SingleInstanceGuard? _singleInstanceGuard;
    private MainWindow? _mainWindow;

    public App()
        : this(singleInstanceGuard: null)
    {
    }

    public App(SingleInstanceGuard? singleInstanceGuard)
    {
        _singleInstanceGuard = singleInstanceGuard;
        InitializeComponent();

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
#if DEBUG
                logging.AddDebug();
#endif
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<ISettingsService, SettingsService>();
                services.AddSingleton<IThemeService, ThemeService>();
                services.AddSingleton<IAdbService, AdbService>();
                services.AddSingleton<IDeviceManagerService, DeviceManagerService>();
                services.AddSingleton<IWindowStateService, WindowStateService>();
                services.AddSingleton<D3D11SwapChainRenderer>();
                services.AddSingleton<IMirrorSessionService, MirrorSessionService>();

                services.AddSingleton<DeviceListViewModel>();
                services.AddSingleton<MirrorViewModel>();
                services.AddSingleton<SettingsViewModel>();

                services.AddSingleton<MainWindow>();
            })
            .Build();
    }

    public static T GetService<T>()
        where T : notnull
    {
        var current = (App)Current;
        return current._host.Services.GetRequiredService<T>();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var settingsService = _host.Services.GetRequiredService<ISettingsService>();
        await settingsService.LoadAsync();

        var themeService = _host.Services.GetRequiredService<IThemeService>();
        themeService.Apply(settingsService.Current.DisplayMode, settingsService.Current.ColorTheme);

        _mainWindow = _host.Services.GetRequiredService<MainWindow>();
        themeService.AttachWindow(_mainWindow);

        _singleInstanceGuard?.StartListening(() =>
        {
            _mainWindow.DispatcherQueue.TryEnqueue(() => _mainWindow.BringToForeground());
        });

        _mainWindow.Activate();
    }
}
