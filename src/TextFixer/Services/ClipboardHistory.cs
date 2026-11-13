using System.Security.Cryptography;
using System.Text.Json;

namespace TextFixer.Services;

internal sealed record ClipboardEntry(string Id, DateTime CopiedAt, string Text, string? Html, string? Rtf)
{
    internal bool HasFormatting => Html is not null || Rtf is not null;
}

/// <summary>The most recent clipboard texts, newest first, stored DPAPI-encrypted for the current Windows user.</summary>
internal sealed class ClipboardHistory : IDisposable
{
    internal const int Capacity = 20;
    private static readonly byte[] Entropy = "TextFixer.ClipboardHistory.v1"u8.ToArray();
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly System.Threading.Timer _saveTimer;
    private List<ClipboardEntry> _entries;
    private bool _dirty;

    internal event EventHandler? Changed;

    internal ClipboardHistory(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TextFixer", "clipboard-history.bin");
        _entries = Load(_path);
        _saveTimer = new System.Threading.Timer(_ => Flush());
    }

    internal IReadOnlyList<ClipboardEntry> Entries { get { lock (_gate) return [.. _entries]; } }

    internal ClipboardEntry? Find(string id) { lock (_gate) return _entries.Find(e => e.Id == id); }

    /// <summary>Records a copy. The same text moves its entry to the top with the new formatting.</summary>
    internal void Add(string text, string? html, string? rtf, DateTime copiedAt)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_gate)
        {
            var existing = _entries.FindIndex(e => e.Text == text);
            var id = existing >= 0 ? _entries[existing].Id : Guid.NewGuid().ToString("N");
            if (existing >= 0) _entries.RemoveAt(existing);
            _entries.Insert(0, new ClipboardEntry(id, copiedAt, text, html, rtf));
            if (_entries.Count > Capacity) _entries.RemoveRange(Capacity, _entries.Count - Capacity);
        }
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
        lock (_gate) { if (_entries.RemoveAll(e => e.Id == id) == 0) return; }
        Touch();
    }

    internal void Clear()
    {
        lock (_gate) { _entries = []; _dirty = false; }
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        try { File.Delete(_path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Changed?.Invoke(this, EventArgs.Empty);
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

    private static List<ClipboardEntry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var plaintext = Dpapi.Unprotect(File.ReadAllBytes(path), Entropy);
            try
            {
                var stored = JsonSerializer.Deserialize<Stored>(plaintext);
                return stored?.Entries?.Where(e => e is { Id: not null, Text: not null } && !string.IsNullOrWhiteSpace(e.Text))
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
