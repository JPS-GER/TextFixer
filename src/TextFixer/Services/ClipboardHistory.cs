using System.Security.Cryptography;
using System.Text.Json;

namespace TextFixer.Services;

/// <summary>
/// One copy. A text entry holds the text with optional HTML and RTF. An image entry keeps its bytes in a separate
/// encrypted file (<see cref="ImageType"/> is set, <see cref="Text"/> is empty). A file entry holds only the paths
/// (<see cref="Files"/>; <see cref="Text"/> lists them one per line), so it depends on the files still being there.
/// </summary>
internal sealed record ClipboardEntry(string Id, DateTime CopiedAt, string Text, string? Html, string? Rtf)
{
    public string? ImageType { get; init; }
    public string? ImageHash { get; init; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    public long ImageBytes { get; init; }
    public string[]? Files { get; init; }
    internal bool HasFormatting => Html is not null || Rtf is not null;
    internal bool IsImage => ImageType is not null;
    internal bool IsFiles => Files is { Length: > 0 };
    internal bool IsText => !IsImage && !IsFiles;
}

/// <summary>The most recent clipboard copies, newest first, stored DPAPI-encrypted for the current Windows user.</summary>
internal sealed class ClipboardHistory : IDisposable
{
    internal const int Capacity = 20;
    /// <summary>All kept images together; older images are dropped first when a new one exceeds it.</summary>
    internal const long MaxImageTotal = 200L * 1024 * 1024;
    private static readonly byte[] Entropy = "TextFixer.ClipboardHistory.v1"u8.ToArray();
    private static readonly byte[] ImageEntropy = "TextFixer.ClipboardImage.v1"u8.ToArray();
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);
    private readonly object _gate = new();
    private readonly string _path, _imageFolder;
    private readonly System.Threading.Timer _saveTimer;
    private List<ClipboardEntry> _entries;
    private bool _dirty;

    internal event EventHandler? Changed;

    internal ClipboardHistory(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TextFixer", "clipboard-history.bin");
        _imageFolder = Path.Combine(Path.GetDirectoryName(_path)!, "clipboard-images");
        _entries = Load(_path, _imageFolder);
        RemoveOrphanImages();
        _saveTimer = new System.Threading.Timer(_ => Flush());
    }

    internal IReadOnlyList<ClipboardEntry> Entries { get { lock (_gate) return [.. _entries]; } }

    internal ClipboardEntry? Find(string id) { lock (_gate) return _entries.Find(e => e.Id == id); }

    /// <summary>Records a text copy. The same text moves its entry to the top with the new formatting.</summary>
    internal void Add(string text, string? html, string? rtf, DateTime copiedAt)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Insert(e => e.IsText && e.Text == text, id => new ClipboardEntry(id, copiedAt, text, html, rtf));
    }

    /// <summary>Records copied files by their paths. The same file list moves its entry to the top.</summary>
    internal void AddFiles(IReadOnlyList<string> files, DateTime copiedAt)
    {
        if (files.Count == 0) return;
        string[] paths = [.. files];
        Insert(e => e.IsFiles && e.Files!.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase),
            id => new ClipboardEntry(id, copiedAt, string.Join("\n", paths), null, null) { Files = paths });
    }

    /// <summary>Records an image (PNG or BMP bytes). The same image moves its entry to the top.</summary>
    internal void AddImage(byte[] data, string type, int width, int height, DateTime copiedAt)
    {
        if (data.Length == 0 || data.Length > MaxImageTotal) return;
        var hash = Convert.ToHexString(SHA256.HashData(data));
        ClipboardEntry? same;
        lock (_gate) same = _entries.Find(e => e.ImageHash == hash);
        if (same is not null) { MoveToTop(same.Id, copiedAt); return; }
        var id = Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(_imageFolder);
            var temp = ImagePath(id) + ".tmp";
            File.WriteAllBytes(temp, Dpapi.Protect(data, ImageEntropy));
            File.Move(temp, ImagePath(id), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { return; }
        Insert(_ => false, _ => new ClipboardEntry(id, copiedAt, "", null, null)
            { ImageType = type, ImageHash = hash, ImageWidth = width, ImageHeight = height, ImageBytes = data.Length }, id);
    }

    /// <summary>The decrypted bytes of an image entry, or null when it is gone or damaged.</summary>
    internal byte[]? ReadImage(string id)
    {
        if (Find(id) is not { IsImage: true }) return null;
        try { return Dpapi.Unprotect(File.ReadAllBytes(ImagePath(id)), ImageEntropy); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { return null; }
    }

    private void Insert(Predicate<ClipboardEntry> same, Func<string, ClipboardEntry> create, string? newId = null)
    {
        List<ClipboardEntry> dropped = [];
        lock (_gate)
        {
            var existing = _entries.FindIndex(same);
            var id = existing >= 0 ? _entries[existing].Id : newId ?? Guid.NewGuid().ToString("N");
            if (existing >= 0) _entries.RemoveAt(existing);
            _entries.Insert(0, create(id));
            if (_entries.Count > Capacity) { dropped.AddRange(_entries.Skip(Capacity)); _entries.RemoveRange(Capacity, _entries.Count - Capacity); }
            // Images over the total size: the oldest go first, the new one stays.
            while (_entries.Where(e => e.IsImage).Sum(e => e.ImageBytes) > MaxImageTotal
                && _entries.FindLastIndex(e => e.IsImage) is var last and > 0)
            { dropped.Add(_entries[last]); _entries.RemoveAt(last); }
        }
        foreach (var entry in dropped) DeleteImage(entry);
        Touch();
    }

    internal void MoveToTop(string id, DateTime copiedAt)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Id == id);
            if (index < 0) return;
            var entry = _entries[index] with { CopiedAt = copiedAt };
            _entries.RemoveAt(index);
            _entries.Insert(0, entry);
        }
        Touch();
    }

    internal void Delete(string id)
    {
        ClipboardEntry? removed;
        lock (_gate)
        {
            removed = _entries.Find(e => e.Id == id);
            if (removed is null) return;
            _entries.Remove(removed);
        }
        DeleteImage(removed);
        Touch();
    }

    internal void Clear()
    {
        lock (_gate) { _entries = []; _dirty = false; }
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        try { File.Delete(_path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { if (Directory.Exists(_imageFolder)) Directory.Delete(_imageFolder, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string ImagePath(string id) => Path.Combine(_imageFolder, id + ".bin");

    private void DeleteImage(ClipboardEntry entry)
    {
        if (!entry.IsImage) return;
        try { File.Delete(ImagePath(entry.Id)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Image files without an entry, left by a crash or a damaged history file.</summary>
    private void RemoveOrphanImages()
    {
        try
        {
            if (!Directory.Exists(_imageFolder)) return;
            var keep = _entries.Where(e => e.IsImage).Select(e => e.Id + ".bin").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(_imageFolder))
                if (!keep.Contains(Path.GetFileName(file))) File.Delete(file);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Touch()
    {
        lock (_gate) _dirty = true;
        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes pending changes now. Failures leave the in-memory history intact.</summary>
    internal void Flush()
    {
        byte[] plaintext;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            plaintext = JsonSerializer.SerializeToUtf8Bytes(new Stored(1, _entries));
        }
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(temp, Dpapi.Protect(plaintext, Entropy));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static List<ClipboardEntry> Load(string path, string imageFolder)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var plaintext = Dpapi.Unprotect(File.ReadAllBytes(path), Entropy);
            try
            {
                var stored = JsonSerializer.Deserialize<Stored>(plaintext);
                return stored?.Entries?.Where(e => e is { Id: not null, Text: not null } && (
                        e.IsImage ? e.ImageType is "image/png" or "image/bmp" && File.Exists(Path.Combine(imageFolder, e.Id + ".bin"))
                        : e.IsFiles || !string.IsNullOrWhiteSpace(e.Text)))
                    .Take(Capacity).ToList() ?? [];
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return []; // A damaged or foreign file starts an empty history.
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Flush();
    }

    private sealed record Stored(int Version, List<ClipboardEntry> Entries);
}
