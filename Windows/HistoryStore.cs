using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DualCopy.Windows;

internal sealed record ClipboardEntry(string Id, string Kind, string? Text, string? Data, long Timestamp);

internal sealed class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const int Limit = 50;
    private readonly object gate = new();
    private readonly string path;
    private List<ClipboardEntry> entries = new();

    public HistoryStore()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualCopy");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "history.json");
        Load();
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

    public bool Merge(IEnumerable<ClipboardEntry> incoming)
    {
        bool changed = false;
        lock (gate)
        {
            HashSet<string> known = entries.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            foreach (ClipboardEntry item in incoming)
            {
                bool valid = item.Kind == "image" ? !string.IsNullOrEmpty(item.Data) : item.Kind == "text" && !string.IsNullOrEmpty(item.Text);
                if (!valid || known.Contains(item.Id)) continue;
                entries.Add(item);
                known.Add(item.Id);
                changed = true;
            }
            if (changed)
            {
                entries = entries.OrderByDescending(item => item.Timestamp).Take(Limit).ToList();
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
            entries = loaded.Where(item => item.Kind == "image" ? !string.IsNullOrEmpty(item.Data) : !string.IsNullOrEmpty(item.Text))
                .OrderByDescending(item => item.Timestamp).Take(Limit).ToList();
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
}
