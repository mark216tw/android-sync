using Com.AndroidSync.App.Models;
using Com.AndroidSync.App.Services;
using Microsoft.UI.Xaml;
using Xunit;

namespace Com.AndroidSync.App.Tests;

public sealed class ThemeServiceTests
{
    [Theory]
    [InlineData(AppDisplayMode.System, ElementTheme.Default)]
    [InlineData(AppDisplayMode.Light, ElementTheme.Light)]
    [InlineData(AppDisplayMode.Dark, ElementTheme.Dark)]
    public void ToElementTheme_MapsDisplayModesCorrectly(AppDisplayMode mode, ElementTheme expected)
    {
        Assert.Equal(expected, ThemeService.ToElementTheme(mode));
    }

    [Fact]
    public void GetThemeDictionaryUri_CoversAllFiveThemesWithUniqueUris()
    {
        var themes = Enum.GetValues<AppColorTheme>();
        Assert.Equal(5, themes.Length);

        var uris = themes.Select(ThemeService.GetThemeDictionaryUri).ToList();
        Assert.Equal(5, uris.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(uris, uri => Assert.StartsWith("ms-appx:///Styles/Themes/", uri, StringComparison.Ordinal));
    }

    [Fact]
    public void GetTitleBarColors_DarkMode_HasHighContrastBetweenBackgroundAndForeground()
    {
        var dark = ThemeService.GetTitleBarColors(isDark: true);
        var bgLuma = dark.Background.R + dark.Background.G + dark.Background.B;
        var fgLuma = dark.Foreground.R + dark.Foreground.G + dark.Foreground.B;

        Assert.True(bgLuma < 200);
        Assert.True(fgLuma > 600);
        Assert.Equal(255, dark.Background.A);
        Assert.Equal(255, dark.HoverBackground.A);
        Assert.Equal(255, dark.PressedBackground.A);
    }

    [Fact]
    public void GetTitleBarColors_LightMode_HasHighContrastBetweenBackgroundAndForeground()
    {
        var light = ThemeService.GetTitleBarColors(isDark: false);
        var bgLuma = light.Background.R + light.Background.G + light.Background.B;
        var fgLuma = light.Foreground.R + light.Foreground.G + light.Foreground.B;

        Assert.True(bgLuma > 600);
        Assert.True(fgLuma < 200);
    }
}
