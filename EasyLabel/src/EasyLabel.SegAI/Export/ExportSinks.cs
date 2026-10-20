using System.IO.Compression;
using System.Text;

namespace EasyLabel.SegAI.Export;

/// <summary>Where an export writes its files: a folder, or one ZIP. Paths are '/' separated and relative to the output.</summary>
internal abstract class ExportSink : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    public List<string> Written { get; } = new List<string>();

    public void WriteText(string rel, string text)
    {
        WriteBytes(rel, Utf8NoBom.GetBytes(text));
    }

    public abstract void WriteBytes(string rel, byte[] bytes);

    public abstract void CopyFile(string rel, string sourcePath);

    /// <summary>Finishes the output (a ZIP is moved into place). Without it the output is discarded by <see cref="Dispose"/>.</summary>
    public abstract void Complete();

    public abstract void Dispose();

    protected static string CheckRel(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || rel.Contains(':') || rel.StartsWith('/') || rel.StartsWith('\\'))
            throw new ArgumentException("Invalid export path: " + rel, nameof(rel));
        string[] parts = rel.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Length == 0 || p == "." || p == ".."))
            throw new ArgumentException("Invalid export path: " + rel, nameof(rel));
        return string.Join("/", parts);
    }
}

internal sealed class FolderExportSink : ExportSink
{
    private readonly string _root;
    private readonly bool _overwrite;

    public FolderExportSink(string root, bool overwrite)
    {
        _root = root;
        _overwrite = overwrite;
        Directory.CreateDirectory(root);
    }

    public override void WriteBytes(string rel, byte[] bytes)
    {
        string path = PathOf(rel);
        using (var stream = new FileStream(path, _overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Write(bytes, 0, bytes.Length);
        Written.Add(path);
    }

    public override void CopyFile(string rel, string sourcePath)
    {
        string path = PathOf(rel);
        File.Copy(sourcePath, path, _overwrite);
        Written.Add(path);
    }

    public override void Complete()
    {
    }

    public override void Dispose()
    {
    }

    private string PathOf(string rel)
    {
        string path = Path.Combine(_root, CheckRel(rel).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}

internal sealed class ZipExportSink : ExportSink
{
    private readonly string _finalPath;
    private readonly string _stagedPath;
    private readonly bool _overwrite;
    private FileStream? _stream;
    private ZipArchive? _zip;
    private bool _completed;

    public ZipExportSink(string finalPath, bool overwrite)
    {
        _finalPath = finalPath;
        _overwrite = overwrite;
        string folder = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(folder);
        _stagedPath = Path.Combine(folder, "." + Path.GetFileName(finalPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        _stream = new FileStream(_stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        _zip = new ZipArchive(_stream, ZipArchiveMode.Create);
    }

    public override void WriteBytes(string rel, byte[] bytes)
    {
        ZipArchiveEntry entry = _zip!.CreateEntry(CheckRel(rel), CompressionLevel.Fastest);
        using (Stream output = entry.Open())
            output.Write(bytes, 0, bytes.Length);
    }

    public override void CopyFile(string rel, string sourcePath)
    {
        ZipArchiveEntry entry = _zip!.CreateEntry(CheckRel(rel), CompressionLevel.Fastest);
        using (Stream output = entry.Open())
        using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            input.CopyTo(output);
    }

    public override void Complete()
    {
        _zip!.Dispose();
        _zip = null;
        _stream!.Dispose();
        _stream = null;
        if (File.Exists(_finalPath) && !_overwrite)
            throw new IOException("The file already exists: " + _finalPath);
        File.Move(_stagedPath, _finalPath, true);
        _completed = true;
        Written.Add(_finalPath);
    }

    public override void Dispose()
    {
        try { _zip?.Dispose(); } catch (IOException) { } catch (InvalidOperationException) { }
        try { _stream?.Dispose(); } catch (IOException) { }
        _zip = null;
        _stream = null;
        if (!_completed)
        {
            try { if (File.Exists(_stagedPath)) File.Delete(_stagedPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
