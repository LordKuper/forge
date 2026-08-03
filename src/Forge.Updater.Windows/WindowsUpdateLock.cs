namespace Forge.Updater.Windows;

public sealed class WindowsUpdateLock(TimeSpan? timeout = null) : IUpdateLock
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan timeout = timeout ?? DefaultTimeout;

    public ValueTask<UpdateLockResult> AcquireAsync(
        UpdateTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(target.OperatingSystem, "windows", StringComparison.Ordinal))
        {
            return ValueTask.FromResult(new UpdateLockResult(
                null,
                new(UpdateDiagnosticCode.PlatformNotSupported, "Windows update locking is unavailable on this platform.")));
        }

        Semaphore semaphore = new(1, 1, "Local\\Forge.Update");
        try
        {
            int result = WaitHandle.WaitAny([semaphore, cancellationToken.WaitHandle], timeout);
            if (result == 0)
            {
                return ValueTask.FromResult(new UpdateLockResult(new Lease(semaphore), UpdateDiagnostic.None));
            }

            semaphore.Dispose();
            if (result == 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return ValueTask.FromResult(new UpdateLockResult(
                null,
                new(UpdateDiagnosticCode.UpdateInProgress, "Another Forge update is already in progress.")));
        }
        catch
        {
            semaphore.Dispose();
            throw;
        }
    }

    private sealed class Lease(Semaphore semaphore) : IAsyncDisposable
    {
        private Semaphore? semaphore = semaphore;

        public ValueTask DisposeAsync()
        {
            Semaphore? current = Interlocked.Exchange(ref semaphore, null);
            if (current is not null)
            {
                current.Release();
                current.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }
}
