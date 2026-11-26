namespace Com.AndroidSync.App.Helpers;

public sealed class ClipboardSyncCoordinator
{
    private readonly Lock _lock = new();
    private string? _lastSyncedText;
    private string? _lastImeInjectedText;

    public bool ShouldApplyFromDevice(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        lock (_lock)
        {
            if (string.Equals(text, _lastImeInjectedText, StringComparison.Ordinal))
            {
                _lastImeInjectedText = null;
                return false;
            }

            if (string.Equals(text, _lastSyncedText, StringComparison.Ordinal))
            {
                return false;
            }

            _lastSyncedText = text;
            return true;
        }
    }

    public bool ShouldSendToDevice(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        lock (_lock)
        {
            if (string.Equals(text, _lastSyncedText, StringComparison.Ordinal))
            {
                return false;
            }

            _lastSyncedText = text;
            return true;
        }
    }

    public void RecordImeInjectedText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_lock)
        {
            _lastImeInjectedText = text;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _lastSyncedText = null;
            _lastImeInjectedText = null;
        }
    }
}
