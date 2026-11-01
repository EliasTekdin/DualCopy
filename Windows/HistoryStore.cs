using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DualCopy.Windows;

internal sealed record ClipboardEntry(string Id, string Text, long Timestamp);

internal sealed class HistoryStore
{
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

    public ClipboardEntry AddLocal(string text)
    {
        ClipboardEntry entry = new(Guid.NewGuid().ToString(), text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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
                if (string.IsNullOrWhiteSpace(item.Text) || known.Contains(item.Id)) continue;
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
            List<ClipboardEntry>? loaded = JsonSerializer.Deserialize<List<ClipboardEntry>>(File.ReadAllText(path));
            if (loaded is null) return;
            entries = loaded.Where(item => !string.IsNullOrWhiteSpace(item.Text))
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
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
        File.Move(temporary, path, true);
    }
}
