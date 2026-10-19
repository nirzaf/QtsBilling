namespace QtsBilling.Persistence.Sqlite;

/// <summary>
/// The single-writer gate for the local database. SQLite allows one writer at a time; serializing writes in
/// process avoids `database is locked` retry storms between the UI and the sync coordinator (QB-17).
/// Reads continue to use WAL snapshots concurrently.
/// </summary>
public sealed class SqliteWriteGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_gate);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        private int _disposed;

        public Releaser(SemaphoreSlim gate) => _gate = gate;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _gate.Release();
            }
        }
    }
}
