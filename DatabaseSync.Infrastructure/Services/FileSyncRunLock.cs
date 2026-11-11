namespace DatabaseSync.Infrastructure.Services;

/// <summary>
/// Cross-process lock so the worker and the Admin site cannot sync at the same time.
/// The operating system releases the lock if the process exits.
/// </summary>
internal sealed class FileSyncRunLock : IDisposable
{
    private readonly FileStream _stream;

    private FileSyncRunLock(FileStream stream) => _stream = stream;

    public static bool TryAcquire(string path, out FileSyncRunLock? handle)
    {
        handle = null;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            handle = new FileSyncRunLock(stream);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose() => _stream.Dispose();
}
