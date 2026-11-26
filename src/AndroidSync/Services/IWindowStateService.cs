using Microsoft.UI.Windowing;

namespace Com.AndroidSync.App.Services;

public interface IWindowStateService
{
    void Restore(AppWindow appWindow);

    Task SaveAsync(AppWindow appWindow, CancellationToken cancellationToken = default);
}
