namespace Fanner.App.Services;

/// <summary>
/// Keeps one Fanner to a logon session, and hands a second launch to the copy that
/// is already running.
/// </summary>
/// <remarks>
/// Duplicate tray icons are the visible half of the problem. The real one is that
/// both copies take the same headers and both write duty cycles, so the fans follow
/// whichever wrote last, and quitting either one hands every header back to the
/// firmware while the other still believes it is driving.
/// <para>
/// The names are session-local, so two users signed in at once get a copy each,
/// which is what they want: the fans are shared but the session is not, and a
/// <c>Global\</c> name would let one user's copy lock out the other's.
/// </para>
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Fanner.SingleInstance";

    private const string ShowWindowName = @"Local\Fanner.ShowWindow";

    /// <summary>
    /// How long a starting copy waits for a running one to let go before deciding it
    /// is a duplicate.
    /// </summary>
    /// <remarks>
    /// This is the elevation handover. "Restart as administrator" starts the new
    /// copy and only then shuts the old one down, so for a moment both are alive and
    /// the new one would otherwise mistake its own parent for a duplicate and exit —
    /// leaving the user with nothing running at all.
    /// </remarks>
    private static readonly TimeSpan HandoverGrace = TimeSpan.FromSeconds(5);

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _showWindow;
    private readonly ManualResetEvent _stopping = new(false);

    private Thread? _listener;

    private SingleInstance(Mutex mutex, EventWaitHandle? showWindow)
    {
        _mutex = mutex;
        _showWindow = showWindow;
    }

    /// <summary>
    /// Claims the right to be the running copy, or returns null if another copy
    /// already holds it — having first asked that copy to show itself, so a second
    /// launch looks like the window coming to the front rather than nothing at all.
    /// </summary>
    public static SingleInstance? Acquire()
    {
        Mutex mutex;

        try
        {
            mutex = new Mutex(false, MutexName);
        }
        catch (UnauthorizedAccessException)
        {
            // The running copy is elevated and this one is not, so the object is
            // labelled above us and even opening it is refused. It exists, which is
            // all we needed to know.
            AskRunningCopyToShowItself();
            return null;
        }

        if (!TryEnter(mutex, TimeSpan.Zero))
        {
            AskRunningCopyToShowItself();

            if (!TryEnter(mutex, HandoverGrace))
            {
                mutex.Dispose();
                return null;
            }
        }

        EventWaitHandle? showWindow;

        try
        {
            showWindow = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowName);
        }
        catch (UnauthorizedAccessException)
        {
            // Without it a later launch cannot raise our window, which is a smaller
            // problem than refusing to start.
            showWindow = null;
        }

        return new SingleInstance(mutex, showWindow);
    }

    /// <summary>
    /// Calls <paramref name="show"/> whenever someone starts Fanner again. Runs on a
    /// background thread, so the handler has to get itself onto the UI thread.
    /// </summary>
    public void ListenForSecondLaunch(Action show)
    {
        if (_showWindow is null || _listener is not null)
        {
            return;
        }

        _listener = new Thread(() =>
        {
            WaitHandle[] handles = [_showWindow, _stopping];

            while (WaitHandle.WaitAny(handles) == 0)
            {
                show();
            }
        })
        {
            Name = "Fanner.SecondLaunch",
            IsBackground = true,
        };

        _listener.Start();
    }

    private static void AskRunningCopyToShowItself()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowWindowName, out var showWindow))
            {
                using (showWindow)
                {
                    showWindow.Set();
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // An elevated copy is running and we cannot reach it. It stays where it
            // is; at least we are not adding a second tray icon.
        }
    }

    private static bool TryEnter(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // The previous copy was killed rather than closed. We now hold it.
            return true;
        }
    }

    public void Dispose()
    {
        _stopping.Set();
        _listener?.Join(TimeSpan.FromSeconds(1));

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (Exception)
        {
            // Either we never owned it, or this is not the thread that took it.
            // Process exit releases it either way, and the next copy treats an
            // abandoned mutex as its own.
        }

        _mutex.Dispose();
        _showWindow?.Dispose();
        _stopping.Dispose();
    }
}
