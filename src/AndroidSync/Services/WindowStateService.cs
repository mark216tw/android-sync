using Com.AndroidSync.App.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Com.AndroidSync.App.Services;

public sealed class WindowStateService : IWindowStateService
{
    private const int MinWidth = 900;
    private const int MinHeight = 620;

    private readonly ISettingsService _settingsService;
    private readonly ILogger<WindowStateService> _logger;

    public WindowStateService(ISettingsService settingsService, ILogger<WindowStateService> logger)
    {
        _settingsService = settingsService;
        _logger = logger;
    }

    public void Restore(AppWindow appWindow)
    {
        var saved = _settingsService.Current.WindowState;
        if (saved is null)
        {
            appWindow.Resize(new SizeInt32(1120, 760));
            return;
        }

        try
        {
            var width = Math.Max(saved.Width, MinWidth);
            var height = Math.Max(saved.Height, MinHeight);
            var targetRect = new RectInt32(saved.X, saved.Y, width, height);

            var displayArea = DisplayArea.GetFromRect(targetRect, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;

            var clampedWidth = Math.Min(width, workArea.Width);
            var clampedHeight = Math.Min(height, workArea.Height);
            var clampedX = Math.Clamp(saved.X, workArea.X, workArea.X + workArea.Width - clampedWidth);
            var clampedY = Math.Clamp(saved.Y, workArea.Y, workArea.Y + workArea.Height - clampedHeight);

            appWindow.MoveAndResize(new RectInt32(clampedX, clampedY, clampedWidth, clampedHeight));

            if (saved.IsMaximized && appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "還原視窗位置失敗，使用預設大小。");
            appWindow.Resize(new SizeInt32(1120, 760));
        }
    }

    public async Task SaveAsync(AppWindow appWindow, CancellationToken cancellationToken = default)
    {
        try
        {
            var isMaximized = appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            var existing = _settingsService.Current.WindowState ?? new WindowPlacementState();

            if (!isMaximized)
            {
                existing.X = appWindow.Position.X;
                existing.Y = appWindow.Position.Y;
                existing.Width = Math.Max(appWindow.Size.Width, MinWidth);
                existing.Height = Math.Max(appWindow.Size.Height, MinHeight);
            }

            existing.IsMaximized = isMaximized;
            _settingsService.Current.WindowState = existing;

            await _settingsService.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "儲存視窗狀態失敗。");
        }
    }
}
