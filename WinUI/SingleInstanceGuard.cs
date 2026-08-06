using System.Threading;

namespace SuperDoc.Sms.WinUI;

/// <summary>
/// Ensures only one copy of the app runs. Two instances would both try to hold the SMS receive
/// registration and both poll the same database, so the second one hands over instead: it
/// signals the first to show its window and then exits.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\MessageT480s.SmsManager.Instance";
    private const string ShowEventName = @"Local\MessageT480s.SmsManager.Show";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _showEvent;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    private SingleInstanceGuard(bool isPrimary, Mutex? mutex, EventWaitHandle? showEvent)
    {
        IsPrimary = isPrimary;
        _mutex = mutex;
        _showEvent = showEvent;
    }

    /// <summary>True when this process is the one that should run the UI and the receiver.</summary>
    public bool IsPrimary { get; }

    /// <summary>Raised on a background thread when another instance asks for the window.</summary>
    public event Action? ShowRequested;

    /// <param name="exclusive">
    /// False for a copy that owns no modem registration, such as a demo window. It reports itself
    /// as primary without taking the mutex, so it neither exits nor summons the running instance.
    /// </param>
    public static SingleInstanceGuard Acquire(bool exclusive = true)
    {
        if (!exclusive)
        {
            return new SingleInstanceGuard(true, null, null);
        }

        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            return new SingleInstanceGuard(true, mutex, showEvent);
        }

        mutex.Dispose();

        // Nudge the running instance, then let this process exit.
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
        }
        catch (Exception)
        {
            // The primary may be shutting down; nothing useful to do but exit quietly.
        }

        return new SingleInstanceGuard(false, null, null);
    }

    /// <summary>Starts listening for "show the window" requests from later instances.</summary>
    public void StartListening()
    {
        if (!IsPrimary || _showEvent is null)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { _showEvent, _cts.Token.WaitHandle };
            while (!_cts.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(handles) == 0)
                {
                    ShowRequested?.Invoke();
                }
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstanceListener"
        };

        thread.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();

        try
        {
            if (_mutex is not null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
        }
        catch (Exception)
        {
            // Releasing a mutex we no longer own is not worth failing shutdown over.
        }

        _showEvent?.Dispose();
        _cts.Dispose();
    }
}
