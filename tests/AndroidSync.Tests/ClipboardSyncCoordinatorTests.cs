using Com.AndroidSync.App.Helpers;
using Xunit;

namespace Com.AndroidSync.App.Tests;

public sealed class ClipboardSyncCoordinatorTests
{
    [Fact]
    public void PreventsEchoLoopsInBothDirections()
    {
        var coordinator = new ClipboardSyncCoordinator();

        // Host copies "abc" -> should send to device
        Assert.True(coordinator.ShouldSendToDevice("abc"));
        // Duplicate host event -> suppressed
        Assert.False(coordinator.ShouldSendToDevice("abc"));
        // Device echoes "abc" back -> suppressed
        Assert.False(coordinator.ShouldApplyFromDevice("abc"));

        // Device copies "xyz" -> should apply to host
        Assert.True(coordinator.ShouldApplyFromDevice("xyz"));
        // Windows ContentChanged fires for "xyz" -> suppressed
        Assert.False(coordinator.ShouldSendToDevice("xyz"));
    }

    [Fact]
    public void SuppressesImeEchoWithoutClobberingHostClipboardState()
    {
        var coordinator = new ClipboardSyncCoordinator();

        Assert.True(coordinator.ShouldSendToDevice("host-clip"));

        // User types Chinese via IME -> injected via device clipboard paste
        coordinator.RecordImeInjectedText("中文");
        // Device auto-syncs "中文" back -> must NOT overwrite Windows clipboard
        Assert.False(coordinator.ShouldApplyFromDevice("中文"));

        // If user later copies "中文" on phone intentionally, it should now apply
        Assert.True(coordinator.ShouldApplyFromDevice("中文"));
    }
}
