using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DualCopy.Windows;

internal static class DualCopyAppearance
{
    public static readonly Color DarkBack = Color.FromArgb(30, 30, 30);
    public static readonly Color DarkControl = Color.FromArgb(45, 45, 48);
    public static readonly Color DarkText = Color.WhiteSmoke;

    public static void Apply(Control control, bool dark)
    {
        control.BackColor = dark ? DarkBack : SystemColors.Control;
        control.ForeColor = dark ? DarkText : SystemColors.ControlText;
        foreach (Control child in control.Controls)
        {
            child.BackColor = dark && child is not Button and not CheckBox and not ComboBox ? DarkControl : (dark ? DarkControl : SystemColors.Window);
            child.ForeColor = dark ? DarkText : SystemColors.ControlText;
            if (child is TextBoxBase textBox) textBox.BackColor = dark ? DarkControl : SystemColors.Window;
            Apply(child, dark);
        }
    }
}

internal sealed class MainForm : Form
{
    private readonly HistoryStore history = new();
    private readonly NotifyIcon trayIcon;
    private readonly System.Windows.Forms.Timer clipboardTimer;
    private readonly SynchronizationContext uiContext;
    private PeerClient peer;
    private DualCopySettings settings;
    private string? lastClipboard;
    private string? lastImageFingerprint;
    private string? lastFileFingerprint;
    private bool applyingRemote;
    private bool exiting;
    private DateTime lastClipboardBusyLogUtc;
    private string lastStatus = "Starting…";
    private readonly Label connectionSummary = new();
    private readonly Label setupSummary = new();
    private readonly ListView historyList = new();
    private readonly TextBox logView = new();
    private readonly System.Windows.Forms.Timer logRefreshTimer;
    private ConnectionCheckForm? connectionCheck;
    private ConfigureForm? settingsEditor;

    public MainForm()
    {
        settings = DualCopySettings.Load(SettingsPath());
        DualCopyLog.Info($"Settings loaded: targetPort={settings.Port}, pollingMs={settings.PollingInterval}, syncImages={settings.SyncImages}, syncFiles={settings.SyncFiles}, imageLimitBytes={settings.MaxImageBytes}, fileLimitBytes={settings.MaxFileBytes}.");
        history.Limit = settings.HistoryLimit;
        uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        peer = new PeerClient(history, settings.Address, settings.Port);

        Text = "DualCopy";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 520);
        Size = new Size(900, 650);
        ShowInTaskbar = true;
        BuildMainInterface();

        trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "DualCopy: starting"
        };
        peer.StatusChanged += status => uiContext.Post(_ => UpdateConnectionStatus(status), null);
        peer.RemoteClipboardReceived += entry => uiContext.Post(_ => { ApplyRemoteClipboard(entry); RefreshHistoryPage(); }, null);

        ContextMenuStrip menu = new();
        menu.Items.Add("Open DualCopy", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { exiting = true; Close(); });
        trayIcon.ContextMenuStrip = menu;
        trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        FormClosing += (_, args) =>
        {
            if (!exiting && args.CloseReason == CloseReason.UserClosing && trayIcon.Visible)
            {
                args.Cancel = true;
                Hide();
                trayIcon.BalloonTipTitle = "DualCopy is still running";
                trayIcon.BalloonTipText = "Open DualCopy from the notification area to view connection status.";
                trayIcon.ShowBalloonTip(2500);
            }
        };
        FormClosed += (_, _) => { trayIcon.Visible = false; trayIcon.Dispose(); peer.Dispose(); logRefreshTimer.Stop(); };
        Shown += (_, _) =>
        {
            RefreshHistoryPage();
            RefreshLogPage();
            ShowConnectionCheck();
        };

        clipboardTimer = new System.Windows.Forms.Timer { Interval = settings.PollingInterval };
        clipboardTimer.Tick += (_, _) => PollClipboard();
        clipboardTimer.Start();
        logRefreshTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        logRefreshTimer.Tick += (_, _) => RefreshLogPage();
        logRefreshTimer.Start();
        peer.Start();
    }

    private void BuildMainInterface()
    {
        TabControl tabs = new() { Dock = DockStyle.Fill, Name = "mainTabs" };
        tabs.TabPages.Add(BuildConnectionTab());
        tabs.TabPages.Add(BuildHistoryTab());
        tabs.TabPages.Add(BuildSettingsTab());
        tabs.TabPages.Add(BuildLogsTab());
        tabs.TabPages.Add(BuildAboutTab());
        Controls.Add(tabs);
        Tag = tabs;
        DualCopyAppearance.Apply(this, settings.DarkMode);
    }

    private TabPage BuildConnectionTab()
    {
        TabPage page = new("Connection");
        Label heading = new() { Text = "Device connection", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Left = 24, Top = 26 };
        connectionSummary.SetBounds(24, 68, 800, 54);
        connectionSummary.AutoEllipsis = true;
        connectionSummary.Font = new Font(Font.FontFamily, 12, FontStyle.Regular);
        setupSummary.SetBounds(24, 130, 800, 110);
        setupSummary.AutoSize = false;
        setupSummary.Text = "Startup checks: Windows listener, saved iPhone address, and peer handshake. A successful TCP connection still depends on both apps being enabled and reachable on the same LAN or routed VPN.";
        Button configure = new() { Text = "Connection and transfer settings…", Left = 24, Top = 260, Width = 250, Height = 36 };
        configure.Click += (_, _) => SelectTab("Settings");
        Button check = new() { Text = "Run connection check", Left = 288, Top = 260, Width = 180, Height = 36 };
        check.Click += (_, _) => ShowConnectionCheck();
        page.Controls.AddRange(new Control[] { heading, connectionSummary, setupSummary, configure, check });
        return page;
    }

    private void ShowConnectionCheck()
    {
        if (connectionCheck is not null && !connectionCheck.IsDisposed) connectionCheck.Close();
        connectionCheck = new ConnectionCheckForm(settings.Address, settings.Port);
        connectionCheck.SettingsRequested += (_, _) => SelectTab("Settings");
        connectionCheck.UpdateStatus(lastStatus, settings.Address, settings.Port);
        connectionCheck.Show(this);
    }

    private TabPage BuildHistoryTab()
    {
        TabPage page = new("History");
        historyList.SetBounds(12, 12, 840, 500);
        historyList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        historyList.View = View.Details; historyList.FullRowSelect = true;
        historyList.Columns.Add("Type", 90); historyList.Columns.Add("Name / preview", 560); historyList.Columns.Add("Time", 160);
        Button copy = new() { Text = "Copy selected", Left = 12, Top = 522, Width = 120, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        copy.Click += (_, _) => CopySelectedHistory();
        Button clear = new() { Text = "Clear history", Left = 142, Top = 522, Width = 120, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        clear.Click += (_, _) => { history.Clear(); RefreshHistoryPage(); };
        page.Controls.AddRange(new Control[] { historyList, copy, clear });
        return page;
    }

    private TabPage BuildSettingsTab()
    {
        TabPage page = new("Settings");
        settingsEditor = CreateSettingsEditor(settings);
        page.Controls.Add(settingsEditor);
        settingsEditor.Show();
        return page;
    }

    private ConfigureForm CreateSettingsEditor(DualCopySettings value)
    {
        ConfigureForm editor = new(value, lastStatus, embedded: true)
        {
            TopLevel = false,
            FormBorderStyle = FormBorderStyle.None,
            Dock = DockStyle.Fill
        };
        editor.SettingsAccepted += (_, _) => ApplySettings(editor.SettingsValue);
        editor.SettingsCancelled += (_, _) => BeginInvoke(new Action(RebuildSettingsEditor));
        return editor;
    }

    private void RebuildSettingsEditor()
    {
        if (settingsEditor is null || settingsEditor.Parent is not TabPage page) return;
        page.Controls.Remove(settingsEditor);
        settingsEditor.Dispose();
        settingsEditor = CreateSettingsEditor(settings);
        page.Controls.Add(settingsEditor);
        settingsEditor.Show();
    }

    private TabPage BuildLogsTab()
    {
        TabPage page = new("Logs");
        logView.SetBounds(12, 12, 840, 470); logView.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        logView.Multiline = true; logView.ReadOnly = true; logView.ScrollBars = ScrollBars.Both; logView.WordWrap = false;
        Button refresh = new() { Text = "Refresh", Left = 12, Top = 492, Width = 90, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        refresh.Click += (_, _) => RefreshLogPage();
        Button folder = new() { Text = "Open log folder", Left = 112, Top = 492, Width = 130, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        folder.Click += (_, _) => DualCopyLog.OpenFolder();
        page.Controls.AddRange(new Control[] { logView, refresh, folder });
        return page;
    }

    private TabPage BuildAboutTab()
    {
        TabPage page = new("About");
        page.Controls.Add(new Label { Text = "DualCopy 0.3.12\\r\\n\\r\\nShare text, images, and files between iPhone and Windows.\\r\\n\\r\\nGitHub: github.com/EliasTekdin/DualCopy", Left = 24, Top = 24, Width = 700, Height = 160 });
        return page;
    }

    private void UpdateConnectionStatus(string status)
    {
        lastStatus = status;
        trayIcon.Text = "DualCopy: " + TrimStatus(status);
        connectionSummary.Text = "Current status: " + status;
        setupSummary.Text = BuildSetupGuidance(status, settings.Address, settings.Port);
        connectionCheck?.UpdateStatus(status, settings.Address, settings.Port);
    }

    private static string BuildSetupGuidance(string status, string address, int port)
    {
        if (string.IsNullOrWhiteSpace(address)) return "Action needed: enter the iPhone's current LAN/VPN IP address and matching listening port in Settings.";
        if (status.StartsWith("Connected", StringComparison.OrdinalIgnoreCase) || status.Contains("Connected to DualCopy", StringComparison.OrdinalIgnoreCase))
            return $"Peer socket is connected to {address}:{port}. Keep both devices enabled and reachable; clipboard exchange starts after the JSONL hello/history handshake.";
        if (status.StartsWith("Listen failed", StringComparison.OrdinalIgnoreCase)) return "Windows could not open its listening port. Check whether another app already uses this port and allow DualCopy through Windows Firewall.";
        if (status.StartsWith("Outgoing connection", StringComparison.OrdinalIgnoreCase) || status.StartsWith("Peer disconnected", StringComparison.OrdinalIgnoreCase))
            return $"Not connected to {address}:{port}. Confirm the iPhone is unlocked recently/has DualCopy enabled, Wi-Fi is active, the address has not changed, and firewall/router rules allow TCP {port}. DualCopy retries automatically.";
        return $"Windows listens on TCP {port} and retries the iPhone endpoint {address}:{port}. Use the same reachable LAN/VPN network; mobile data alone cannot reach a private home address.";
    }

    private void RefreshHistoryPage()
    {
        if (historyList.IsDisposed) return;
        historyList.BeginUpdate(); historyList.Items.Clear();
        foreach (ClipboardEntry entry in history.Snapshot())
        {
            string value = entry.Kind == "text" ? entry.Text ?? "" : entry.Name ?? entry.Kind;
            if (value.Length > 100) value = value[..100];
            historyList.Items.Add(new ListViewItem(new[] { entry.Kind, value, DateTimeOffset.FromUnixTimeMilliseconds(entry.Timestamp).ToLocalTime().ToString("g") }) { Tag = entry });
        }
        historyList.EndUpdate();
    }

    private void CopySelectedHistory()
    {
        if (historyList.SelectedItems.Count == 0 || historyList.SelectedItems[0].Tag is not ClipboardEntry entry) return;
        try
        {
            if (entry.Kind == "text" && entry.Text is not null) Clipboard.SetText(entry.Text);
            else if (entry.Kind == "image" && entry.Data is not null)
            {
                using MemoryStream stream = new(Convert.FromBase64String(entry.Data)); using Image source = Image.FromStream(stream); using Bitmap bitmap = new(source); Clipboard.SetImage(bitmap);
            }
            else if (entry.Kind == "file" && entry.Data is not null)
            {
                Directory.CreateDirectory(settings.ReceiveFolder); string path = Path.Combine(settings.ReceiveFolder, Path.GetFileName(entry.Name ?? "DualCopy-file.bin")); File.WriteAllBytes(path, Convert.FromBase64String(entry.Data)); Clipboard.SetFileDropList(new StringCollection { path });
            }
        }
        catch (Exception error) { DualCopyLog.Error("Could not copy a history entry to the Windows clipboard.", error); }
    }

    private void RefreshLogPage()
    {
        if (logView.IsDisposed || !Visible) return;
        try
        {
            string path = Path.Combine(DualCopyLog.FolderPath, "DualCopy.log");
            string contents = File.Exists(path) ? File.ReadAllText(path) : "Log file will appear after the first diagnostic event.";
            if (logView.Text != contents) { logView.Text = contents; logView.SelectionStart = logView.TextLength; logView.ScrollToCaret(); }
        }
        catch (Exception error) { logView.Text = "Could not read the log file: " + error.Message; }
    }

    private void SelectTab(string name)
    {
        if (Tag is TabControl tabs)
            foreach (TabPage tab in tabs.TabPages) if (tab.Text == name) { tabs.SelectedTab = tab; break; }
    }

    private void ShowMainWindow()
    {
        Show(); WindowState = FormWindowState.Normal; Activate();
    }

    private void PollClipboard()
    {
        if (applyingRemote) return;
        try
        {
            if (settings.SyncFiles && Clipboard.ContainsFileDropList())
            {
                List<FilePayload> files = ReadFilePayloads(Clipboard.GetFileDropList());
                string fingerprint = FileFingerprint(files);
                if (files.Count > 0 && fingerprint != lastFileFingerprint)
                {
                    lastFileFingerprint = fingerprint;
                    lastClipboard = null;
                    lastImageFingerprint = null;
                    DualCopyLog.Info($"Local clipboard change detected: {files.Count} file(s), totalBytes={files.Sum(file => (long)file.Data.Length)}.");
                    foreach (FilePayload file in files)
                    {
                        ClipboardEntry entry = history.AddFile(file.Name, Convert.ToBase64String(file.Data), file.Mime);
                        _ = peer.SendClipboardAsync(entry);
                    }
                }
                return;
            }

            if (settings.SyncImages && Clipboard.ContainsImage())
            {
                using Image? image = Clipboard.GetImage();
                string? fingerprint = ImageFingerprint(image);
                byte[]? data = ImageToData(image, settings);
                if (image is not null && fingerprint is not null && data is { Length: > 0 } && fingerprint != lastImageFingerprint)
                {
                    lastImageFingerprint = fingerprint;
                    lastFileFingerprint = null;
                    lastClipboard = null;
                    DualCopyLog.Info($"Local clipboard image detected: {image.Width}x{image.Height}, encodedBytes={data.Length}.");
                    _ = peer.SendClipboardAsync(history.AddImage(Convert.ToBase64String(data)));
                }
                else if (image is not null && fingerprint is not null && data is null)
                {
                    DualCopyLog.Warn($"Local clipboard image could not be encoded within the configured limit ({settings.MaxImageBytes} bytes).");
                }
                return;
            }

            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text) && text != lastClipboard)
                {
                    lastClipboard = text;
                    lastImageFingerprint = null;
                    lastFileFingerprint = null;
                    DualCopyLog.Info($"Local clipboard text detected: {text.Length} characters; sending entry.");
                    _ = peer.SendClipboardAsync(history.AddText(text));
                }
            }
        }
        catch (ExternalException error)
        {
            // Another process owns the clipboard; the next timer tick retries. Avoid log spam under contention.
            if (DateTime.UtcNow - lastClipboardBusyLogUtc > TimeSpan.FromSeconds(30))
            {
                lastClipboardBusyLogUtc = DateTime.UtcNow;
                DualCopyLog.Warn("Windows clipboard was temporarily unavailable; polling will retry.", error);
            }
        }
        catch (IOException error)
        {
            trayIcon.Text = "DualCopy: could not read a file";
            DualCopyLog.Error("Could not read a local clipboard file.", error);
        }
        catch (Exception error)
        {
            DualCopyLog.Error("Unexpected error while polling the Windows clipboard.", error);
        }
    }

    private void ApplyRemoteClipboard(ClipboardEntry entry)
    {
        DualCopyLog.Info($"Remote clipboard entry received: kind={entry.Kind}, id={entry.Id}.");
        try
        {
            applyingRemote = true;
            if (entry.Kind == "image" && !string.IsNullOrEmpty(entry.Data))
            {
                if (!settings.SyncImages) { DualCopyLog.Info("Ignored remote image because image syncing is disabled in Windows settings."); return; }
                byte[] data = Convert.FromBase64String(entry.Data);
                if (settings.MaxImageBytes > 0 && data.Length > settings.MaxImageBytes)
                {
                    DualCopyLog.Warn($"Ignored remote image of {data.Length} bytes; configured maximum is {settings.MaxImageBytes} bytes.");
                    return;
                }
                using MemoryStream stream = new(data);
                using Image source = Image.FromStream(stream);
                using Bitmap bitmap = new(source);
                Clipboard.SetImage(bitmap);
                lastImageFingerprint = ImageFingerprint(bitmap);
                lastFileFingerprint = null;
                lastClipboard = null;
                DualCopyLog.Info($"Applied remote image to the Windows clipboard ({data.Length} bytes).");
            }
            else if (entry.Kind == "file" && !string.IsNullOrEmpty(entry.Data))
            {
                if (!settings.SyncFiles) { DualCopyLog.Info("Ignored remote file because file syncing is disabled in Windows settings."); return; }
                byte[] data = Convert.FromBase64String(entry.Data);
                if (settings.MaxFileBytes > 0 && data.Length > settings.MaxFileBytes)
                {
                    DualCopyLog.Warn($"Ignored remote file of {data.Length} bytes; configured maximum is {settings.MaxFileBytes} bytes.");
                    return;
                }
                string path = SaveReceivedFile(entry.Name, data);
                Clipboard.SetFileDropList(new StringCollection { path });
                lastFileFingerprint = FileFingerprint(new[] { new FilePayload(Path.GetFileName(path), data, MimeForPath(path)) });
                lastImageFingerprint = null;
                lastClipboard = null;
                trayIcon.Text = "DualCopy: received " + TrimStatus(Path.GetFileName(path));
                DualCopyLog.Info($"Applied remote file to the Windows clipboard ({data.Length} bytes).");
            }
            else if (entry.Kind == "text" && !string.IsNullOrEmpty(entry.Text))
            {
                Clipboard.SetText(entry.Text);
                lastClipboard = entry.Text;
                lastImageFingerprint = null;
                lastFileFingerprint = null;
                DualCopyLog.Info($"Applied remote text to the Windows clipboard ({entry.Text.Length} characters).");
            }
        }
        catch (ExternalException error)
        {
            trayIcon.Text = "DualCopy: could not write clipboard";
            DualCopyLog.Error("Could not apply a remote entry to the Windows clipboard.", error);
        }
        catch (FormatException error)
        {
            trayIcon.Text = "DualCopy: invalid data received";
            DualCopyLog.Error("Remote clipboard entry contained invalid base64 data.", error);
        }
        catch (ArgumentException error)
        {
            trayIcon.Text = "DualCopy: unsupported image or file";
            DualCopyLog.Error("Windows rejected a remote image or file payload.", error);
        }
        catch (Exception error)
        {
            DualCopyLog.Error("Unexpected error while applying a remote clipboard entry.", error);
        }
        finally
        {
            applyingRemote = false;
        }
    }

    private string SaveReceivedFile(string? requestedName, byte[] data)
    {
        Directory.CreateDirectory(settings.ReceiveFolder);
        string safeName = Path.GetFileName(string.IsNullOrWhiteSpace(requestedName) ? "DualCopy-file.bin" : requestedName);
        if (string.IsNullOrWhiteSpace(safeName) || safeName is "." or "..") safeName = "DualCopy-file.bin";
        string path = Path.Combine(settings.ReceiveFolder, safeName);
        if (File.Exists(path))
        {
            string stem = Path.GetFileNameWithoutExtension(safeName);
            string extension = Path.GetExtension(safeName);
            path = Path.Combine(settings.ReceiveFolder, $"{stem}-{DateTime.Now:yyyyMMdd-HHmmssfff}{extension}");
        }
        if (settings.ConvertRawImages && Path.GetExtension(path).Equals(".dng", StringComparison.OrdinalIgnoreCase) && TryConvertImage(data, settings, out byte[] converted))
        {
            path = Path.ChangeExtension(path, settings.ImageFormat == "jpeg" ? ".jpg" : ".png");
            data = converted;
        }
        File.WriteAllBytes(path, data);
        return path;
    }

    private void ShowConfigure()
    {
        using ConfigureForm dialog = new(settings, lastStatus);
        if (dialog.ShowDialog(this) == DialogResult.OK) ApplySettings(dialog.SettingsValue);
    }

    private void ApplySettings(DualCopySettings updated)
    {
        settings = updated.Normalized();
        settings.Save(SettingsPath());
        history.Limit = settings.HistoryLimit;
        clipboardTimer.Interval = settings.PollingInterval;
        peer.Configure(settings.Address, settings.Port);
        DualCopyLog.Info($"Settings saved: target={settings.Address}:{settings.Port}, pollingMs={settings.PollingInterval}, syncImages={settings.SyncImages}, syncFiles={settings.SyncFiles}.");
        UpdateConnectionStatus(lastStatus);
        peer.Start();
    }

    private void ShowHistory()
    {
        using HistoryForm dialog = new(history, settings);
        dialog.ShowDialog(this);
    }

    private static void ShowAbout()
    {
        using AboutForm dialog = new();
        dialog.ShowDialog();
    }

    private static string TrimStatus(string status) => status.Length > 55 ? status[..55] : status;
    private static string SettingsPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualCopy", "settings.json");

    private List<FilePayload> ReadFilePayloads(StringCollection paths)
    {
        List<FilePayload> result = new();
        foreach (string path in paths.Cast<string>().Take(20))
        {
            if (!File.Exists(path)) continue;
            FileInfo info = new(path);
            if (settings.MaxFileBytes > 0 && info.Length > settings.MaxFileBytes) continue;
            byte[] data = File.ReadAllBytes(path);
            string name = Path.GetFileName(path);
            if (settings.ConvertRawImages && Path.GetExtension(path).Equals(".dng", StringComparison.OrdinalIgnoreCase) && TryConvertImage(data, settings, out byte[] converted))
            {
                data = converted;
                name = Path.ChangeExtension(name, settings.ImageFormat == "jpeg" ? ".jpg" : ".png");
            }
            result.Add(new FilePayload(name, data, MimeForPath(name)));
        }
        return result;
    }

    private static string FileFingerprint(IEnumerable<FilePayload> files)
    {
        using SHA256 sha = SHA256.Create();
        using MemoryStream stream = new();
        foreach (FilePayload file in files)
        {
            byte[] name = System.Text.Encoding.UTF8.GetBytes(file.Name);
            stream.Write(name);
            stream.Write(file.Data);
        }
        return Convert.ToHexString(sha.ComputeHash(stream.ToArray()));
    }

    private static string? ImageFingerprint(Image? image)
    {
        if (image is null || image.Width <= 0 || image.Height <= 0) return null;
        using Bitmap normalized = new(image.Width, image.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(normalized))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(image, new Rectangle(0, 0, normalized.Width, normalized.Height));
        }
        using MemoryStream stream = new();
        normalized.Save(stream, ImageFormat.Png);
        byte[] digest = SHA256.HashData(stream.ToArray());
        return $"{normalized.Width}x{normalized.Height}:{Convert.ToHexString(digest)}";
    }

    private static byte[]? ImageToData(Image? image, DualCopySettings options)
    {
        if (image is null) return null;
        using MemoryStream stream = new();
        ImageFormat format = options.ImageFormat == "jpeg" ? ImageFormat.Jpeg : ImageFormat.Png;
        if (format == ImageFormat.Jpeg) SaveAsJpeg(image, stream, options.JpegQuality);
        else image.Save(stream, format);
        byte[] data = stream.ToArray();
        if (options.MaxImageBytes == 0 || data.Length <= options.MaxImageBytes) return data;
        if (options.ImageFormat == "auto")
        {
            stream.SetLength(0);
            SaveAsJpeg(image, stream, options.JpegQuality);
            data = stream.ToArray();
            if (data.Length <= options.MaxImageBytes) return data;
        }
        return null;
    }

    private static void SaveAsJpeg(Image image, Stream output, float quality)
    {
        ImageCodecInfo? codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(item => item.FormatID == ImageFormat.Jpeg.Guid);
        if (codec is null)
        {
            image.Save(output, ImageFormat.Jpeg);
            return;
        }
        using EncoderParameters parameters = new(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Round(Math.Clamp(quality, 0.40f, 1.0f) * 100));
        image.Save(output, codec, parameters);
    }

    private static bool TryConvertImage(byte[] source, DualCopySettings options, out byte[] converted)
    {
        converted = Array.Empty<byte>();
        try
        {
            using MemoryStream input = new(source);
            using Image image = Image.FromStream(input);
            byte[]? result = ImageToData(image, options);
            if (result is null) return false;
            converted = result;
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (ExternalException) { return false; }
    }

    private static string MimeForPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".txt" => "text/plain",
        ".dng" => "image/x-adobe-dng",
        _ => "application/octet-stream"
    };

    private sealed record FilePayload(string Name, byte[] Data, string Mime);
}

internal sealed class ConnectionCheckForm : Form
{
    private readonly Label status = new();
    private readonly Label guidance = new();
    private readonly string address;
    private readonly int port;
    public event EventHandler? SettingsRequested;

    public ConnectionCheckForm(string address, int port)
    {
        this.address = address;
        this.port = port;
        Text = "DualCopy connection check";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(480, 190);
        Label title = new() { Text = "Checking DualCopy connection", Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Left = 18, Top = 16, Width = 430, Height = 28 };
        status.SetBounds(18, 52, 440, 28);
        guidance.SetBounds(18, 82, 440, 54);
        guidance.AutoEllipsis = true;
        Button settings = new() { Text = "Review setup", Left = 18, Top = 145, Width = 110, Height = 30 };
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        Button close = new() { Text = "Close", Left = 368, Top = 145, Width = 90, Height = 30 };
        close.Click += (_, _) => Close();
        Controls.AddRange(new Control[] { title, status, guidance, settings, close });
        UpdateStatus("Starting connection services", address, port);
    }

    public void UpdateStatus(string currentStatus, string targetAddress, int targetPort)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => UpdateStatus(currentStatus, targetAddress, targetPort))); return; }
        status.Text = "Stage: " + currentStatus;
        if (string.IsNullOrWhiteSpace(targetAddress))
            guidance.Text = "Setup needed: enter the iPhone's current IP address and matching port. The two devices need a shared LAN or routed VPN path.";
        else if (currentStatus.Contains("Connected", StringComparison.OrdinalIgnoreCase))
            guidance.Text = $"Peer reached at {targetAddress}:{targetPort}. If clipboard sync does not start, check both devices' Sync enabled settings and inspect the Logs tab.";
        else if (currentStatus.StartsWith("Listen failed", StringComparison.OrdinalIgnoreCase))
            guidance.Text = $"Windows could not listen on port {targetPort}; another process or firewall policy may be blocking it. Open Settings to review the port.";
        else
            guidance.Text = $"Trying to reach {targetAddress}:{targetPort}. Check the iPhone is powered on with DualCopy enabled, on Wi-Fi, and still using this address. Retries continue automatically.";
    }
}

internal sealed class ConfigureForm : Form
{
    private readonly TextBox addressBox = new();
    private readonly NumericUpDown portBox = new();
    private readonly NumericUpDown intervalBox = new();
    private readonly NumericUpDown historyBox = new();
    private readonly NumericUpDown imageBytesBox = new();
    private readonly NumericUpDown fileBytesBox = new();
    private readonly NumericUpDown qualityBox = new();
    private readonly ComboBox formatBox = new();
    private readonly TextBox folderBox = new();
    private readonly CheckBox imagesBox = new();
    private readonly CheckBox filesBox = new();
    private readonly CheckBox rawBox = new();
    private readonly CheckBox darkBox = new();
    private readonly Label statusLabel = new();
    private readonly ToolTip optionsToolTip = new();
    private readonly DualCopySettings initial;
    public event EventHandler? SettingsAccepted;
    public event EventHandler? SettingsCancelled;

    public DualCopySettings SettingsValue => initial with
    {
        Address = addressBox.Text.Trim(),
        Port = (int)portBox.Value,
        PollingInterval = (int)intervalBox.Value,
        HistoryLimit = (int)historyBox.Value,
        MaxImageBytes = (long)imageBytesBox.Value,
        MaxFileBytes = (long)fileBytesBox.Value,
        ImageFormat = formatBox.Text,
        JpegQuality = (float)qualityBox.Value,
        ReceiveFolder = folderBox.Text.Trim(),
        SyncImages = imagesBox.Checked,
        SyncFiles = filesBox.Checked,
        ConvertRawImages = rawBox.Checked,
        DarkMode = darkBox.Checked
    };

    public ConfigureForm(DualCopySettings settings, string status, bool embedded = false)
    {
        initial = settings;
        Text = "DualCopy settings";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = embedded ? FormBorderStyle.None : FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(590, 500);

        AddLabel("iPhone IP address", 16, 18); addressBox.SetBounds(220, 14, 340, 24); addressBox.Text = settings.Address; Controls.Add(addressBox);
        AddLabel("iPhone listening port", 16, 50); ConfigureNumber(portBox, 220, 46, 65535, settings.Port, 100); AddControl(portBox);
        AddLabel("Polling interval (ms)", 16, 82); ConfigureNumber(intervalBox, 220, 78, 5000, settings.PollingInterval, 100); AddControl(intervalBox);
        AddLabel("History limit", 16, 114); ConfigureNumber(historyBox, 220, 110, 1000, settings.HistoryLimit, 10); AddControl(historyBox);
        AddLabel("Max image bytes (0 = unlimited)", 16, 146); ConfigureNumber(imageBytesBox, 220, 142, int.MaxValue, settings.MaxImageBytes, 0); AddControl(imageBytesBox);
        AddLabel("Max file bytes (0 = unlimited)", 16, 178); ConfigureNumber(fileBytesBox, 220, 174, int.MaxValue, settings.MaxFileBytes, 0); AddControl(fileBytesBox);
        AddLabel("Image format", 16, 210); formatBox.SetBounds(220, 206, 120, 24); formatBox.DropDownStyle = ComboBoxStyle.DropDownList; formatBox.Items.AddRange(new object[] { "auto", "png", "jpeg" }); formatBox.SelectedItem = settings.ImageFormat; Controls.Add(formatBox);
        AddLabel("JPEG quality", 16, 242); qualityBox.SetBounds(220, 238, 120, 24); qualityBox.DecimalPlaces = 2; qualityBox.Increment = 0.01M; qualityBox.Minimum = 0.40M; qualityBox.Maximum = 1.00M; qualityBox.Value = (decimal)Math.Clamp(settings.JpegQuality, 0.40f, 1.0f); Controls.Add(qualityBox);
        AddLabel("Receive folder", 16, 274); folderBox.SetBounds(220, 270, 340, 24); folderBox.Text = settings.ReceiveFolder; Controls.Add(folderBox);

        imagesBox.Text = "Sync images"; imagesBox.Checked = settings.SyncImages; imagesBox.SetBounds(16, 312, 180, 24); Controls.Add(imagesBox);
        filesBox.Text = "Sync files"; filesBox.Checked = settings.SyncFiles; filesBox.SetBounds(220, 312, 180, 24); Controls.Add(filesBox);
        rawBox.Text = "Convert RAW/DNG files to images when supported"; rawBox.Checked = settings.ConvertRawImages; rawBox.SetBounds(16, 344, 544, 28); rawBox.AutoSize = false; Controls.Add(rawBox);
        optionsToolTip.SetToolTip(rawBox, "When a RAW/DNG file can be decoded by the device, convert it to a viewable PNG/JPEG image. Otherwise DualCopy transfers it unchanged as a file.");
        darkBox.Text = "Dark mode"; darkBox.Checked = settings.DarkMode; darkBox.SetBounds(16, 378, 180, 24); Controls.Add(darkBox);

        statusLabel.SetBounds(16, 414, 544, 48); statusLabel.AutoEllipsis = true; statusLabel.Text = "Last connection status: " + status + "  •  Reconnects automatically; the iPhone pauses outbound attempts when Wi-Fi is unavailable."; Controls.Add(statusLabel);
        Button save = new() { Text = "Save", DialogResult = DialogResult.OK, Left = 400, Top = 462, Width = 75 };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 485, Top = 462, Width = 75 };
        save.Click += (_, _) => SettingsAccepted?.Invoke(this, EventArgs.Empty);
        cancel.Click += (_, _) => SettingsCancelled?.Invoke(this, EventArgs.Empty);
        Controls.Add(save); Controls.Add(cancel); AcceptButton = save; CancelButton = cancel;
        darkBox.CheckedChanged += (_, _) => DualCopyAppearance.Apply(this, darkBox.Checked);
        DualCopyAppearance.Apply(this, settings.DarkMode);
    }

    private void AddLabel(string text, int left, int top) => Controls.Add(new Label { Text = text, Left = left, Top = top + 4, AutoSize = true });
    private void AddControl(Control control) => Controls.Add(control);
    private static void ConfigureNumber(NumericUpDown box, int left, int top, decimal maximum, long value, decimal minimum)
    {
        box.SetBounds(left, top, 150, 24); box.Minimum = minimum; box.Maximum = maximum; box.Value = Math.Clamp((decimal)value, minimum, maximum);
    }
}

internal sealed class HistoryForm : Form
{
    private readonly HistoryStore history;
    private readonly ListView list = new();
    private readonly DualCopySettings settings;

    public HistoryForm(HistoryStore history, DualCopySettings settings)
    {
        this.history = history; this.settings = settings;
        Text = "DualCopy clipboard history"; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(700, 430);
        list.Dock = DockStyle.Fill; list.View = View.Details; list.FullRowSelect = true; list.Columns.Add("Type", 90); list.Columns.Add("Name / preview", 470); list.Columns.Add("Time", 120); Controls.Add(list);
        FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft };
        Button clear = new() { Text = "Clear history", Width = 100 }; clear.Click += (_, _) => { history.Clear(); RefreshEntries(); };
        Button copy = new() { Text = "Copy selected", Width = 100 }; copy.Click += (_, _) => CopySelected();
        Button close = new() { Text = "Close", Width = 80, DialogResult = DialogResult.OK }; buttons.Controls.Add(close); buttons.Controls.Add(clear); buttons.Controls.Add(copy); Controls.Add(buttons);
        RefreshEntries(); DualCopyAppearance.Apply(this, settings.DarkMode);
    }

    private void RefreshEntries()
    {
        list.Items.Clear();
        foreach (ClipboardEntry entry in history.Snapshot())
        {
            string value = entry.Kind == "text" ? entry.Text ?? "" : entry.Name ?? entry.Kind;
            if (value.Length > 90) value = value[..90];
            list.Items.Add(new ListViewItem(new[] { entry.Kind, value, DateTimeOffset.FromUnixTimeMilliseconds(entry.Timestamp).ToLocalTime().ToString("g") }) { Tag = entry });
        }
    }

    private void CopySelected()
    {
        if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not ClipboardEntry entry) return;
        try
        {
            if (entry.Kind == "text" && entry.Text is not null) Clipboard.SetText(entry.Text);
            else if (entry.Kind == "image" && entry.Data is not null)
            {
                using MemoryStream stream = new(Convert.FromBase64String(entry.Data)); using Image source = Image.FromStream(stream); using Bitmap bitmap = new(source); Clipboard.SetImage(bitmap);
            }
            else if (entry.Kind == "file" && entry.Data is not null)
            {
                Directory.CreateDirectory(settings.ReceiveFolder); string path = Path.Combine(settings.ReceiveFolder, Path.GetFileName(entry.Name ?? "DualCopy-file.bin")); File.WriteAllBytes(path, Convert.FromBase64String(entry.Data)); Clipboard.SetFileDropList(new StringCollection { path });
            }
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "DualCopy", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}

internal sealed class AboutForm : Form
{
    public AboutForm()
    {
        Text = "About DualCopy"; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(390, 210); Padding = new Padding(18);
        Label text = new() { Dock = DockStyle.Fill, Text = "DualCopy\n\nVersion 0.3.12\nShare text, images, and files between iPhone and Windows.\n\nAuthor: Ilyas Tekdin / DualCopy contributors\nGitHub: github.com/EliasTekdin/DualCopy", AutoSize = false };
        Controls.Add(text); Button close = new() { Text = "Close", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 30 }; Controls.Add(close);
    }
}
