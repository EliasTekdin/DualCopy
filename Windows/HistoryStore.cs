using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace DualCopy.Windows;

internal sealed record ClipboardEntry(
    string Id,
    string Kind,
    string? Text,
    string? Data,
    long Timestamp,
    string? Name = null,
    string? Uti = null,
    string? Mime = null);

internal sealed class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly object gate = new();
    private readonly string path;
    private List<ClipboardEntry> entries = new();
    private int limit = 50;

    public HistoryStore()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualCopy");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "history.json");
        Load();
    }

    public int Limit
    {
        get { lock (gate) return limit; }
        set
        {
            lock (gate)
            {
                limit = Math.Clamp(value, 10, 1000);
                entries = entries.OrderByDescending(item => item.Timestamp).Take(limit).ToList();
                SaveLocked();
            }
        }
    }

    public ClipboardEntry? Newest
    {
        get { lock (gate) return entries.FirstOrDefault(); }
    }

    public IReadOnlyList<ClipboardEntry> Snapshot()
    {
        lock (gate) return entries.ToArray();
    }

    public ClipboardEntry AddText(string text)
    {
        ClipboardEntry entry = new(Guid.NewGuid().ToString(), "text", text, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Merge(new[] { entry });
        return entry;
    }

    public ClipboardEntry AddImage(string data)
    {
        ClipboardEntry entry = new(Guid.NewGuid().ToString(), "image", null, data, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Merge(new[] { entry });
        return entry;
    }

    public ClipboardEntry AddFile(string name, string data, string? mime)
    {
        ClipboardEntry entry = new(Guid.NewGuid().ToString(), "file", null, data,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), name, MimeToUti(mime), mime);
        Merge(new[] { entry });
        return entry;
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            SaveLocked();
        }
    }

    public bool Merge(IEnumerable<ClipboardEntry> incoming)
    {
        bool changed = false;
        lock (gate)
        {
            HashSet<string> known = entries.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            foreach (ClipboardEntry item in incoming)
            {
                bool valid = item.Kind switch
                {
                    "image" or "file" => !string.IsNullOrEmpty(item.Data),
                    "text" => !string.IsNullOrEmpty(item.Text),
                    _ => false
                };
                if (!valid || known.Contains(item.Id)) continue;
                entries.Add(item);
                known.Add(item.Id);
                changed = true;
            }
            if (changed)
            {
                entries = entries.OrderByDescending(item => item.Timestamp).Take(limit).ToList();
                SaveLocked();
            }
        }
        return changed;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(path)) return;
            List<ClipboardEntry>? loaded = JsonSerializer.Deserialize<List<ClipboardEntry>>(File.ReadAllText(path), JsonOptions);
            if (loaded is null) return;
            entries = loaded.Where(item => item.Kind switch
            {
                "image" or "file" => !string.IsNullOrEmpty(item.Data),
                "text" => !string.IsNullOrEmpty(item.Text),
                _ => false
            }).OrderByDescending(item => item.Timestamp).Take(limit).ToList();
        }
        catch
        {
            entries = new();
        }
    }

    private void SaveLocked()
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temporary, path, true);
    }

    private static string MimeToUti(string? mime) => mime?.ToLowerInvariant() switch
    {
        "application/pdf" => "com.adobe.pdf",
        "application/zip" => "public.zip-archive",
        "text/plain" => "public.plain-text",
        "image/jpeg" => "public.jpeg",
        "image/png" => "public.png",
        _ => "public.data"
    };
}
