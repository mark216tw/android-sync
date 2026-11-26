using System.Runtime.InteropServices;
using Com.AndroidSync.App.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace Com.AndroidSync.App.Services;

public sealed class ThemeService : IThemeService
{
    private const string ThemeDictionaryPathPrefix = "ms-appx:///Styles/Themes/";
    private const int DwmwaUseImmersiveDarkMode = 20;

    public readonly record struct TitleBarColors(
        Color Background,
        Color Foreground,
        Color InactiveForeground,
        Color HoverForeground,
        Color HoverBackground,
        Color PressedForeground,
        Color PressedBackground);

    private readonly ILogger<ThemeService> _logger;
    private readonly UISettings _uiSettings = new();
    private Window? _window;
    private ResourceDictionary? _activeThemeDictionary;
    private bool _subscribedSystemTheme;

    public ThemeService(ILogger<ThemeService> logger)
    {
        _logger = logger;
    }

    public AppDisplayMode CurrentDisplayMode { get; private set; } = AppDisplayMode.System;

    public AppColorTheme CurrentColorTheme { get; private set; } = AppColorTheme.ClassicBlue;

    public static ElementTheme ToElementTheme(AppDisplayMode displayMode) => displayMode switch
    {
        AppDisplayMode.Light => ElementTheme.Light,
        AppDisplayMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    public static string GetThemeDictionaryUri(AppColorTheme colorTheme) => colorTheme switch
    {
        AppColorTheme.EyeCareYellow => $"{ThemeDictionaryPathPrefix}EyeCareYellowTheme.xaml",
        AppColorTheme.ForestGreen => $"{ThemeDictionaryPathPrefix}ForestGreenTheme.xaml",
        AppColorTheme.TwilightPurple => $"{ThemeDictionaryPathPrefix}TwilightPurpleTheme.xaml",
        AppColorTheme.NeutralGray => $"{ThemeDictionaryPathPrefix}NeutralGrayTheme.xaml",
        _ => $"{ThemeDictionaryPathPrefix}ClassicBlueTheme.xaml"
    };

    public void AttachWindow(Window window)
    {
        _window = window;

        if (!_subscribedSystemTheme)
        {
            _uiSettings.ColorValuesChanged += OnSystemColorValuesChanged;
            _subscribedSystemTheme = true;
        }

        Apply(CurrentDisplayMode, CurrentColorTheme);
    }

    public void ApplyDisplayMode(AppDisplayMode displayMode)
    {
        CurrentDisplayMode = displayMode;
        ApplyDisplayModeInternal();
    }

    public void ApplyColorTheme(AppColorTheme colorTheme)
    {
        CurrentColorTheme = colorTheme;
        SwapThemeDictionary(colorTheme);
        ForceVisualTreeThemeRefresh();
    }

    public void Apply(AppDisplayMode displayMode, AppColorTheme colorTheme)
    {
        CurrentDisplayMode = displayMode;
        CurrentColorTheme = colorTheme;
        SwapThemeDictionary(colorTheme);
        ApplyDisplayModeInternal();
        ForceVisualTreeThemeRefresh();
    }

    private void ApplyDisplayModeInternal()
    {
        if (_window?.Content is not FrameworkElement root)
        {
            return;
        }

        root.RequestedTheme = ToElementTheme(CurrentDisplayMode);
        UpdateTitleBarColors(root);
    }

    private void SwapThemeDictionary(AppColorTheme colorTheme)
    {
        if (Application.Current is null)
        {
            return;
        }

        var merged = Application.Current.Resources.MergedDictionaries;
        var targetUri = new Uri(GetThemeDictionaryUri(colorTheme));

        // Remove any previously loaded theme dictionary (including the default one from App.xaml).
        if (_activeThemeDictionary is not null)
        {
            merged.Remove(_activeThemeDictionary);
            _activeThemeDictionary = null;
        }
        else
        {
            for (var i = merged.Count - 1; i >= 0; i--)
            {
                var source = merged[i].Source?.OriginalString;
                if (source is not null && source.StartsWith(ThemeDictionaryPathPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    merged.RemoveAt(i);
                }
            }
        }

        var newDictionary = new ResourceDictionary { Source = targetUri };
        merged.Add(newDictionary);
        _activeThemeDictionary = newDictionary;
        _logger.LogInformation("已套用主題資源字典: {Theme}", colorTheme);
    }

    private void ForceVisualTreeThemeRefresh()
    {
        if (_window?.Content is not FrameworkElement root)
        {
            return;
        }

        // Toggling RequestedTheme forces WinUI 3 to re-evaluate all {ThemeResource} bindings immediately.
        var target = ToElementTheme(CurrentDisplayMode);
        var temporary = root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        root.RequestedTheme = temporary;
        root.RequestedTheme = target;
        UpdateTitleBarColors(root);
    }

    public static TitleBarColors GetTitleBarColors(bool isDark, Color? backgroundOverride = null)
    {
        if (isDark)
        {
            return new TitleBarColors(
                Background: backgroundOverride ?? Color.FromArgb(255, 32, 32, 32),
                Foreground: Colors.White,
                InactiveForeground: Color.FromArgb(255, 160, 160, 160),
                HoverForeground: Colors.White,
                HoverBackground: Color.FromArgb(255, 51, 51, 51),
                PressedForeground: Color.FromArgb(255, 220, 220, 220),
                PressedBackground: Color.FromArgb(255, 69, 69, 69));
        }

        return new TitleBarColors(
            Background: backgroundOverride ?? Color.FromArgb(255, 243, 243, 243),
            Foreground: Color.FromArgb(255, 24, 24, 24),
            InactiveForeground: Color.FromArgb(255, 120, 120, 120),
            HoverForeground: Colors.Black,
            HoverBackground: Color.FromArgb(255, 229, 229, 229),
            PressedForeground: Color.FromArgb(255, 48, 48, 48),
            PressedBackground: Color.FromArgb(255, 212, 212, 212));
    }

    private void UpdateTitleBarColors(FrameworkElement root)
    {
        if (_window is null || !AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var isDark = CurrentDisplayMode switch
        {
            AppDisplayMode.Dark => true,
            AppDisplayMode.Light => false,
            _ => IsSystemDarkMode(root)
        };

        ApplyDwmDarkMode(isDark);

        var palette = GetTitleBarColors(isDark, ResolveTitleBarBackgroundColor(isDark));
        var titleBar = _window.AppWindow.TitleBar;

        titleBar.BackgroundColor = palette.Background;
        titleBar.InactiveBackgroundColor = palette.Background;
        titleBar.ForegroundColor = palette.Foreground;
        titleBar.InactiveForegroundColor = palette.InactiveForeground;

        titleBar.ButtonBackgroundColor = palette.Background;
        titleBar.ButtonInactiveBackgroundColor = palette.Background;
        titleBar.ButtonForegroundColor = palette.Foreground;
        titleBar.ButtonHoverForegroundColor = palette.HoverForeground;
        titleBar.ButtonPressedForegroundColor = palette.PressedForeground;
        titleBar.ButtonInactiveForegroundColor = palette.InactiveForeground;
        titleBar.ButtonHoverBackgroundColor = palette.HoverBackground;
        titleBar.ButtonPressedBackgroundColor = palette.PressedBackground;
    }

    private bool IsSystemDarkMode(FrameworkElement root)
    {
        try
        {
            var bg = _uiSettings.GetColorValue(UIColorType.Background);
            return (bg.R + bg.G + bg.B) < 384;
        }
        catch
        {
            return root.ActualTheme == ElementTheme.Dark;
        }
    }

    private Color? ResolveTitleBarBackgroundColor(bool isDark)
    {
        var themeKey = isDark ? "Dark" : "Light";
        if (_activeThemeDictionary?.ThemeDictionaries.TryGetValue(themeKey, out var subDictObj) == true
            && subDictObj is ResourceDictionary subDict
            && subDict.TryGetValue("AppWindowBackgroundBrush", out var brushObj)
            && brushObj is SolidColorBrush brush)
        {
            return brush.Color;
        }

        return null;
    }

    private void ApplyDwmDarkMode(bool isDark)
    {
        if (_window is null)
        {
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(_window);
        if (hwnd == nint.Zero)
        {
            return;
        }

        var useDarkMode = isDark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDarkMode, sizeof(int));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

    private void OnSystemColorValuesChanged(UISettings sender, object args)
    {
        if (CurrentDisplayMode != AppDisplayMode.System || _window is null)
        {
            return;
        }

        _window.DispatcherQueue.TryEnqueue(() =>
        {
            if (_window.Content is FrameworkElement root && CurrentDisplayMode == AppDisplayMode.System)
            {
                root.RequestedTheme = ElementTheme.Default;
                UpdateTitleBarColors(root);
            }
        });
    }
}
