using System;
using System.IO;
using System.Text.Json;

namespace DualCopy.Windows;

internal sealed record DualCopySettings(
    string Address,
    int Port,
    int PollingInterval,
    int HistoryLimit,
    bool SyncImages,
    bool SyncFiles,
    long MaxImageBytes,
    long MaxFileBytes,
    string ImageFormat,
    float JpegQuality,
    string ReceiveFolder,
    bool DarkMode,
    bool ConvertRawImages = true,
    bool OpenInterfaceOnLaunch = true)
{
    public static DualCopySettings Defaults => new(
        "192.168.1.1",
        49732,
        400,
        50,
        true,
        true,
        4 * 1024 * 1024,
        50 * 1024 * 1024,
        "auto",
        0.82f,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DualCopy"),
        false);

    public DualCopySettings Normalized()
    {
        DualCopySettings defaults = Defaults;
        string format = ImageFormat?.Trim().ToLowerInvariant() ?? "auto";
        if (format is not ("auto" or "png" or "jpeg")) format = "auto";
        return this with
        {
            Address = string.IsNullOrWhiteSpace(Address) ? defaults.Address : Address.Trim(),
            Port = Port is > 0 and <= 65535 ? Port : defaults.Port,
            PollingInterval = Math.Clamp(PollingInterval, 100, 5000),
            HistoryLimit = Math.Clamp(HistoryLimit, 10, 1000),
            MaxImageBytes = Math.Max(0, MaxImageBytes),
            MaxFileBytes = Math.Max(0, MaxFileBytes),
            ImageFormat = format,
            JpegQuality = Math.Clamp(JpegQuality, 0.40f, 1.0f),
            ReceiveFolder = string.IsNullOrWhiteSpace(ReceiveFolder) ? defaults.ReceiveFolder : ReceiveFolder.Trim()
        };
    }

    public static DualCopySettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                DualCopySettings? loaded = JsonSerializer.Deserialize<DualCopySettings>(File.ReadAllText(path));
                if (loaded is not null) return loaded.Normalized();
            }
        }
        catch { }
        return Defaults;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(Normalized(), new JsonSerializerOptions { WriteIndented = true }));
    }
}
