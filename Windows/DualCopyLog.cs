using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DualCopy.Windows;

internal static class DualCopyLog
{
    private const long MaximumLogBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualCopy", "Logs");
    private static readonly string LogPath = Path.Combine(DirectoryPath, "DualCopy.log");

    public static string FolderPath => DirectoryPath;

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? error = null) => Write("WARN", message, error);
    public static void Error(string message, Exception? error = null) => Write("ERROR", message, error);

    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            Process.Start(new ProcessStartInfo { FileName = DirectoryPath, UseShellExecute = true });
            Info("Opened the log folder.");
        }
        catch (Exception error)
        {
            Error("Could not open the log folder.", error);
        }
    }

    private static void Write(string level, string message, Exception? error)
    {
        try
        {
            string line = $"{DateTimeOffset.UtcNow:O} [{level}] {message}";
            if (error is not null)
                line += $" | {error.GetType().Name}: {error.Message.Replace('\r', ' ').Replace('\n', ' ')}";
            line += Environment.NewLine;

            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length + Encoding.UTF8.GetByteCount(line) > MaximumLogBytes)
                    File.Move(LogPath, LogPath + ".1", true);
                File.AppendAllText(LogPath, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never interrupt clipboard synchronisation.
        }
    }
}
