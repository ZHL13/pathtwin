using System.Threading;
using PathTwin.App.Constants;

namespace PathTwin.App.Platform;

public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex? _mutex;

    private SingleInstanceService(Mutex? mutex, bool hasHandle)
    {
        _mutex = mutex;
        HasHandle = hasHandle;
    }

    public bool HasHandle { get; }

    public static SingleInstanceService TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, AppConstants.SingleInstanceMutexName, out var createdNew);
        if (createdNew)
        {
            return new SingleInstanceService(mutex, hasHandle: true);
        }

        mutex.Dispose();
        return new SingleInstanceService(null, hasHandle: false);
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex may already be released during shutdown.
        }

        _mutex.Dispose();
    }
}
