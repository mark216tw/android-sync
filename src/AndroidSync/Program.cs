using Com.AndroidSync.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace Com.AndroidSync.App;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstance = new SingleInstanceGuard();
        if (!singleInstance.IsPrimaryInstance)
        {
            singleInstance.SignalPrimaryInstance();
            return;
        }

        ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(singleInstance);
        });
    }
}
