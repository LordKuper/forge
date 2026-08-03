namespace Forge.Configuration;

internal static class AtomicConfigurationFile
{
    public static async Task WriteAsync(
        string path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken,
        bool retainPrevious = true)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException("Configuration path has no directory.");
        Directory.CreateDirectory(directory);
        string tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(
                    tempPath,
                    fullPath,
                    retainPrevious ? $"{fullPath}.previous" : null,
                    true);
            }
            else
            {
                File.Move(tempPath, fullPath);
            }

            DirectoryFlusher.Flush(directory);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
