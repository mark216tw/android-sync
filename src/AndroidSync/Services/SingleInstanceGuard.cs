using System.Runtime.InteropServices;

namespace Com.AndroidSync.App.Services;

public sealed class SingleInstanceGuard : IDisposable
{
    public const string DefaultMutexName = @"Local\Com.AndroidSync.App.SingleInstanceMutex";
    public const string DefaultActivateEventName = @"Local\Com.AndroidSync.App.ActivateEvent";

    private const uint AsfwAny = unchecked((uint)-1);

    private readonly string _activateEventName;
    private readonly Mutex _mutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _registeredWait;
    private bool _disposed;

    public SingleInstanceGuard(
        string mutexName = DefaultMutexName,
        string activateEventName = DefaultActivateEventName)
    {
        _activateEventName = activateEventName;
        _mutex = new Mutex(initiallyOwned: true, name: mutexName, out var createdNew);
        IsPrimaryInstance = createdNew;
    }

    public bool IsPrimaryInstance { get; }

    public void StartListening(Action onActivateRequested)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsPrimaryInstance || _registeredWait is not null)
        {
            return;
        }

        _activateEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: _activateEventName);

        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, timedOut) =>
            {
                if (!timedOut && !_disposed)
                {
                    onActivateRequested();
                }
            },
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public bool SignalPrimaryInstance()
    {
        AllowSetForegroundWindow(AsfwAny);

        if (EventWaitHandle.TryOpenExisting(_activateEventName, out var existingEvent))
        {
            using (existingEvent)
            {
                return existingEvent.Set();
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _registeredWait?.Unregister(null);
        _registeredWait = null;

        _activateEvent?.Dispose();
        _activateEvent = null;

        if (IsPrimaryInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Mutex may be disposed from a different thread during shutdown.
            }
        }

        _mutex.Dispose();
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);
}
