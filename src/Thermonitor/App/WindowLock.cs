using Thermonitor.Platform;

namespace Thermonitor.App;

/// <summary>
/// One Thermonitor window per session. A second launch waits for the first window to exist and
/// brings it to the front instead of starting everything again (UAC, server...).
/// </summary>
internal sealed class WindowLock : IDisposable
{
    private const string Name = @"Local\Thermonitor.Window";
    private readonly Mutex _mutex;

    private WindowLock(Mutex mutex) => _mutex = mutex;

    /// <summary>The lock, or null when another window was focused (or kept us waiting too long): then exit.</summary>
    public static WindowLock? Acquire(TimeSpan maxWait)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, Name);
        }
        catch (UnauthorizedAccessException)
        {
            // Created by a process with other rights: treat it as another running window.
            Win32.FocusOtherInstance();
            return null;
        }

        var deadline = Environment.TickCount64 + (long)maxWait.TotalMilliseconds;
        while (true)
        {
            try
            {
                if (mutex.WaitOne(0)) return new WindowLock(mutex);
            }
            catch (AbandonedMutexException)
            {
                return new WindowLock(mutex);   // the previous window's process died: the lock is ours
            }

            // The owner may still be starting (UAC prompt, server start-up): wait for its window.
            if (Win32.FocusOtherInstance() || Environment.TickCount64 > deadline)
            {
                Log.Write("another Thermonitor window is running or starting: exiting");
                mutex.Dispose();
                return null;
            }
            Thread.Sleep(250);
        }
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
