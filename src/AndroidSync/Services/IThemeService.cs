using Com.AndroidSync.App.Models;
using Microsoft.UI.Xaml;

namespace Com.AndroidSync.App.Services;

public interface IThemeService
{
    AppDisplayMode CurrentDisplayMode { get; }

    AppColorTheme CurrentColorTheme { get; }

    void AttachWindow(Window window);

    void ApplyDisplayMode(AppDisplayMode displayMode);

    void ApplyColorTheme(AppColorTheme colorTheme);

    void Apply(AppDisplayMode displayMode, AppColorTheme colorTheme);
}
