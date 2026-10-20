using System.Text;

namespace EasyLabel.Core.Io;

public static class AtomicFile
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static void WriteAllText(string path, string contents)
    {
        WriteAllText(path, contents, Utf8NoBom);
    }

    public static void WriteAllText(string path, string contents, Encoding encoding)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path is required.", nameof(path));
        }

        string fullPath = Path.GetFullPath(path);
        string? pathDirectory = Path.GetDirectoryName(fullPath);
        string directory;
        if (string.IsNullOrWhiteSpace(pathDirectory))
        {
            directory = Directory.GetCurrentDirectory();
            fullPath = Path.Combine(directory, fullPath);
        }
        else
        {
            directory = pathDirectory;
        }

        Directory.CreateDirectory(directory);

        string tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(tempPath, contents, encoding);

            if (File.Exists(fullPath))
            {
                File.Replace(tempPath, fullPath, null);
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
