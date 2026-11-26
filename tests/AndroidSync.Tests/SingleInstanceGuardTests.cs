using Com.AndroidSync.App.Services;
using Xunit;

namespace Com.AndroidSync.App.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void SecondInstance_DetectsExistingMutexAndSignalsPrimary()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mutexName = $@"Local\AndroidSync.Test.Mutex.{suffix}";
        var eventName = $@"Local\AndroidSync.Test.Event.{suffix}";

        using var activated = new ManualResetEventSlim(false);
        using var primary = new SingleInstanceGuard(mutexName, eventName);
        Assert.True(primary.IsPrimaryInstance);

        primary.StartListening(() => activated.Set());

        using var secondary = new SingleInstanceGuard(mutexName, eventName);
        Assert.False(secondary.IsPrimaryInstance);

        var signaled = secondary.SignalPrimaryInstance();
        Assert.True(signaled);
        Assert.True(activated.Wait(TimeSpan.FromSeconds(3)));
    }
}
