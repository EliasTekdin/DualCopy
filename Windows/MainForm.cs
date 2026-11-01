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
    public static readonly Color LightBack = Color.FromArgb(244, 247, 252);
    public static readonly Color LightSurface = Color.White;
    public static readonly Color LightText = Color.FromArgb(28, 36, 52);
    public static readonly Color DarkBack = Color.FromArgb(15, 23, 42);
    public static readonly Color DarkSurface = Color.FromArgb(26, 38, 61);
    public static readonly Color DarkText = Color.FromArgb(239, 245, 255);
    public static readonly Color Accent = Color.FromArgb(69, 121, 255);
    public static readonly Color AccentDark = Color.FromArgb(112, 164, 255);

    public static void Apply(Control control, bool dark)
    {
        Color background = dark ? DarkBack : LightBack;
        Color surface = dark ? DarkSurface : LightSurface;
        Color text = dark ? DarkText : LightText;
        Color input = dark ? Color.FromArgb(35, 50, 78) : Color.White;
        Color buttonSurface = dark ? Color.FromArgb(47, 76, 132) : Color.FromArgb(231, 237, 250);
        control.BackColor = control switch
        {
            TextBoxBase => input,
            NumericUpDown => input,
            ComboBox => input,
            Button => buttonSurface,
            GroupBox or Panel or TabControl or TabPage => surface,
            _ => background
        };
        control.ForeColor = text;

        foreach (Control child in control.Controls)
        {
            child.ForeColor = text;
            child.BackColor = child is GroupBox or Panel or TabControl or TabPage ? surface : background;
            if (child is TextBoxBase textBox)
            {
                textBox.BackColor = dark ? Color.FromArgb(35, 50, 78) : Color.White;
                textBox.ForeColor = text;
                textBox.BorderStyle = BorderStyle.FixedSingle;
            }
            else if (child is NumericUpDown number)
            {
                number.BackColor = dark ? Color.FromArgb(35, 50, 78) : Color.White;
                number.ForeColor = text;
            }
            else if (child is ComboBox combo)
            {
                combo.BackColor = dark ? Color.FromArgb(35, 50, 78) : Color.White;
                combo.ForeColor = text;
                combo.FlatStyle = FlatStyle.Flat;
            }
            else if (child is Button button)
            {
                button.BackColor = dark ? Color.FromArgb(47, 76, 132) : Color.FromArgb(231, 237, 250);
                button.ForeColor = text;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = dark ? Color.FromArgb(91, 128, 196) : Color.FromArgb(196, 209, 235);
            }
            else if (child is LinkLabel link)
            {
                link.LinkColor = dark ? AccentDark : Accent;
                link.ActiveLinkColor = dark ? Color.White : Color.FromArgb(35, 83, 190);
            }
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
    private string lastStatus = "Starting…";

    public MainForm()
    {
        settings = DualCopySettings.Load(SettingsPath());
        history.Limit = settings.HistoryLimit;
        uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        peer = new PeerClient(history, settings.Address, settings.Port);

        trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "DualCopy: connecting"
        };
        peer.StatusChanged += status => uiContext.Post(_ =>
        {
            lastStatus = status;
            trayIcon.Text = "DualCopy: " + TrimStatus(status);
        }, null);
        peer.RemoteClipboardReceived += entry => uiContext.Post(_ => ApplyRemoteClipboard(entry), null);

        ContextMenuStrip menu = new();
        menu.Items.Add("Configure", null, (_, _) => ShowConfigure());
        menu.Items.Add("Clipboard history", null, (_, _) => ShowHistory());
        menu.Items.Add("About DualCopy", null, (_, _) => ShowAbout());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());
        trayIcon.ContextMenuStrip = menu;
        trayIcon.DoubleClick += (_, _) => ShowConfigure();

        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        WindowState = FormWindowState.Minimized;
        Opacity = 0;
        Width = 1;
        Height = 1;
        Load += (_, _) => Hide();
        FormClosed += (_, _) => { trayIcon.Visible = false; trayIcon.Dispose(); peer.Dispose(); };

        clipboardTimer = new System.Windows.Forms.Timer { Interval = settings.PollingInterval };
        clipboardTimer.Tick += (_, _) => PollClipboard();
        clipboardTimer.Start();
        peer.Start();
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
                if (fingerprint is not null && data is { Length: > 0 } && fingerprint != lastImageFingerprint)
                {
                    lastImageFingerprint = fingerprint;
                    lastFileFingerprint = null;
                    lastClipboard = null;
                    _ = peer.SendClipboardAsync(history.AddImage(Convert.ToBase64String(data)));
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
                    _ = peer.SendClipboardAsync(history.AddText(text));
                }
            }
        }
        catch (ExternalException)
        {
            // Another process owns the clipboard; the next timer tick retries.
        }
        catch (IOException)
        {
            trayIcon.Text = "DualCopy: could not read a file";
        }
    }

    private void ApplyRemoteClipboard(ClipboardEntry entry)
    {
        try
        {
            applyingRemote = true;
            if (entry.Kind == "image" && !string.IsNullOrEmpty(entry.Data))
            {
                if (!settings.SyncImages) return;
                byte[] data = Convert.FromBase64String(entry.Data);
                if (settings.MaxImageBytes > 0 && data.Length > settings.MaxImageBytes) return;
                using MemoryStream stream = new(data);
                using Image source = Image.FromStream(stream);
                using Bitmap bitmap = new(source);
                Clipboard.SetImage(bitmap);
                lastImageFingerprint = ImageFingerprint(bitmap);
                lastFileFingerprint = null;
                lastClipboard = null;
            }
            else if (entry.Kind == "file" && !string.IsNullOrEmpty(entry.Data))
            {
                if (!settings.SyncFiles) return;
                byte[] data = Convert.FromBase64String(entry.Data);
                if (settings.MaxFileBytes > 0 && data.Length > settings.MaxFileBytes) return;
                string path = SaveReceivedFile(entry.Name, data);
                Clipboard.SetFileDropList(new StringCollection { path });
                lastFileFingerprint = FileFingerprint(new[] { new FilePayload(Path.GetFileName(path), data, MimeForPath(path)) });
                lastImageFingerprint = null;
                lastClipboard = null;
                trayIcon.Text = "DualCopy: received " + TrimStatus(Path.GetFileName(path));
            }
            else if (entry.Kind == "text" && !string.IsNullOrEmpty(entry.Text))
            {
                Clipboard.SetText(entry.Text);
                lastClipboard = entry.Text;
                lastImageFingerprint = null;
                lastFileFingerprint = null;
            }
        }
        catch (ExternalException)
        {
            trayIcon.Text = "DualCopy: could not write clipboard";
        }
        catch (FormatException)
        {
            trayIcon.Text = "DualCopy: invalid data received";
        }
        catch (ArgumentException)
        {
            trayIcon.Text = "DualCopy: unsupported image or file";
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
        using ConfigureForm dialog = new(settings, lastStatus, (address, port) => peer.TestConnectionAsync(address, port));
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        settings = dialog.SettingsValue.Normalized();
        settings.Save(SettingsPath());
        history.Limit = settings.HistoryLimit;
        clipboardTimer.Interval = settings.PollingInterval;
        peer.Configure(settings.Address, settings.Port);
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
    private readonly Button testButton = new();
    private readonly DualCopySettings initial;
    private readonly Func<string, int, Task<string>> testConnection;

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

    public ConfigureForm(DualCopySettings settings, string status, Func<string, int, Task<string>> testConnection)
    {
        initial = settings;
        this.testConnection = testConnection;
        Text = "DualCopy · Settings";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(790, 700);
        Padding = new Padding(0);

        Panel header = new() { Dock = DockStyle.Top, Height = 92, Padding = new Padding(28, 18, 24, 12) };
        Label title = new() { Text = "DualCopy", Font = new Font(Font.FontFamily, 18, FontStyle.Bold), AutoSize = true, Location = new Point(28, 15) };
        Label subtitle = new() { Text = "A calm, reliable bridge between your iPhone and Windows clipboard", AutoSize = true, Location = new Point(30, 51) };
        header.Controls.Add(title); header.Controls.Add(subtitle); Controls.Add(header);

        TabControl tabs = new() { Dock = DockStyle.Fill, Padding = new Point(18, 8), Margin = new Padding(18) };
        tabs.TabPages.Add(BuildConnectionTab());
        tabs.TabPages.Add(BuildContentTab());
        tabs.TabPages.Add(BuildPerformanceTab());
        tabs.TabPages.Add(BuildAppearanceTab());
        Controls.Add(tabs);

        Panel footer = new() { Dock = DockStyle.Bottom, Height = 74, Padding = new Padding(24, 10, 24, 12) };
        statusLabel.Text = "Status: " + status;
        statusLabel.AutoEllipsis = true;
        statusLabel.SetBounds(24, 12, 360, 24);
        testButton.Text = "Test connection";
        testButton.SetBounds(24, 38, 132, 28);
        testButton.Click += async (_, _) =>
        {
            testButton.Enabled = false;
            statusLabel.Text = "Status: testing connection…";
            statusLabel.Text = "Status: " + await this.testConnection(addressBox.Text.Trim(), (int)portBox.Value);
            testButton.Enabled = true;
        };
        Button save = new() { Text = "Save changes", DialogResult = DialogResult.OK, Width = 112, Height = 30 };
        save.Location = new Point(520, 25);
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 88, Height = 30 };
        cancel.Location = new Point(644, 25);
        footer.Controls.Add(statusLabel); footer.Controls.Add(testButton); footer.Controls.Add(save); footer.Controls.Add(cancel);
        Controls.Add(footer);
        AcceptButton = save;
        CancelButton = cancel;

        darkBox.CheckedChanged += (_, _) => DualCopyAppearance.Apply(this, darkBox.Checked);
        DualCopyAppearance.Apply(this, settings.DarkMode);
    }

    private TabPage BuildConnectionTab()
    {
        TabPage page = NewTab("Connection");
        page.Controls.Add(CreateSection("Phone ↔ PC connection", "Both devices use the same TCP channel. The iPhone listens for incoming connections while also connecting to this Windows address.", 310, out TableLayoutPanel table));
        AddSetting(table, "iPhone IP address", "The iPhone’s local LAN or VPN IPv4 address. Windows uses this address when it initiates a connection. Example: 192.168.68.52.", addressBox, settingsValue: initial.Address);
        ConfigureNumber(portBox, 1, 65535, initial.Port);
        AddSetting(table, "iPhone listening port", "The port where the iPhone accepts DualCopy traffic. It must match the iPhone’s Listen port in Settings. The default is 49732.", portBox);
        return page;
    }

    private TabPage BuildContentTab()
    {
        TabPage page = NewTab("Content");
        FlowLayoutPanel flow = NewFlow();
        flow.Controls.Add(CreateSection("Images", "Images are transferred as PNG, JPEG, or the smallest compatible representation. RAW conversion is best effort and never runs on the iPhone’s main UI thread.", 392, out TableLayoutPanel images));
        imagesBox.Text = "Enable image synchronisation"; imagesBox.Checked = initial.SyncImages;
        AddSetting(images, "Sync images", "When enabled, copied pictures travel in both directions. Disabling it prevents image data from using memory, battery, and network bandwidth.", imagesBox);
        rawBox.Text = "Convert RAW images when possible"; rawBox.Checked = initial.ConvertRawImages;
        AddSetting(images, "RAW conversion", "Attempts to turn DNG and other RAW camera data into a normal image. It improves compatibility, but conversion can be CPU- and memory-intensive; when decoding fails, the original file is retained.", rawBox);
        formatBox.DropDownStyle = ComboBoxStyle.DropDownList; formatBox.Items.AddRange(new object[] { "auto", "png", "jpeg" }); formatBox.SelectedItem = initial.ImageFormat;
        AddSetting(images, "Output image format", "Auto prefers a compatible representation and can fall back to JPEG when the size limit is reached. PNG preserves lossless detail; JPEG is smaller but lossy.", formatBox);
        qualityBox.DecimalPlaces = 2; qualityBox.Increment = 0.01M; qualityBox.Minimum = 0.40M; qualityBox.Maximum = 1.00M; qualityBox.Value = (decimal)Math.Clamp(initial.JpegQuality, 0.40f, 1.0f);
        AddSetting(images, "JPEG quality (0.40–1.00)", "This is a fraction, not a file size: 0.40 means roughly 40% encoder quality and smaller files with more artefacts; 0.82 is the default balance; 1.00 keeps the most detail but usually creates larger files. Values below 0.40 are raised to 0.40 and values above 1.00 are reduced to 1.00. PNG ignores this value.", qualityBox);
        ConfigureNumber(imageBytesBox, 0, int.MaxValue, initial.MaxImageBytes);
        AddSetting(images, "Maximum image size", "The largest encoded image DualCopy will send. Set 0 for unlimited on Windows. A limit reduces freezes and memory pressure, but an image above it will be skipped or may still be rejected by the iPhone’s own safety limit.", imageBytesBox, "bytes; 0 = unlimited");
        flow.Controls.Add(CreateSection("Files", "Files are copied as file entries and are saved in the receive folder. This includes TXT, PDF, ZIP, DEB and DNG when the source application exposes file data.", 300, out TableLayoutPanel files));
        filesBox.Text = "Enable file synchronisation"; filesBox.Checked = initial.SyncFiles;
        AddSetting(files, "Sync files", "When enabled, copied files travel in both directions. Disabling it is useful when you only want text and pictures or want to reduce network traffic.", filesBox);
        ConfigureNumber(fileBytesBox, 0, int.MaxValue, initial.MaxFileBytes);
        AddSetting(files, "Maximum file size", "The largest file DualCopy will read and transmit. Set 0 for unlimited on Windows, but remember that very large files consume RAM and may be refused by the iPhone’s configured limit.", fileBytesBox, "bytes; 0 = unlimited");
        folderBox.Text = initial.ReceiveFolder;
        AddSetting(files, "Receive folder", "Windows writes files received from the iPhone here. The default is Downloads\\DualCopy. The folder is created automatically when the first file arrives.", folderBox);
        page.Controls.Add(flow);
        return page;
    }

    private TabPage BuildPerformanceTab()
    {
        TabPage page = NewTab("Performance");
        page.Controls.Add(CreateSection("Responsiveness and storage", "These controls trade detection speed and retained history against battery, CPU, memory, and disk usage.", 310, out TableLayoutPanel table));
        ConfigureNumber(intervalBox, 100, 5000, initial.PollingInterval);
        AddSetting(table, "Polling interval", "How often the Windows app checks for a new clipboard item. 500 ms is the default balance. Lower values feel faster but use more CPU; higher values save resources but add delay.", intervalBox, "milliseconds");
        ConfigureNumber(historyBox, 10, 1000, initial.HistoryLimit);
        AddSetting(table, "History limit", "How many recent entries DualCopy keeps for synchronisation and the Clipboard history window. A larger value makes more past items available but uses more disk, memory, and connection time.", historyBox, "entries");
        return page;
    }

    private TabPage BuildAppearanceTab()
    {
        TabPage page = NewTab("Appearance");
        page.Controls.Add(CreateSection("Visual style", "The application uses a soft light surface by default and a deep midnight palette when dark mode is enabled. This changes the Windows interface only; it does not alter clipboard behaviour.", 250, out TableLayoutPanel table));
        darkBox.Text = "Use midnight dark mode"; darkBox.Checked = initial.DarkMode;
        AddSetting(table, "Theme", "Light mode uses a bright white and blue workspace. Midnight mode uses a dark navy surface with high-contrast text, which can be more comfortable in a dark room. Switch any time; it is saved with the rest of the settings.", darkBox);
        Label note = new() { Text = "Tip: use the tray icon’s Clipboard history command to review, copy, or clear saved entries.", AutoSize = false, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        table.Controls.Add(note, 0, table.RowCount); table.SetColumnSpan(note, 2); table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); table.RowCount++;
        return page;
    }

    private static TabPage NewTab(string name) => new() { Text = name, Padding = new Padding(14, 14, 14, 12), BackColor = DualCopyAppearance.LightBack };

    private static FlowLayoutPanel NewFlow() => new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(2) };

    private static GroupBox CreateSection(string title, string intro, int height, out TableLayoutPanel table)
    {
        GroupBox section = new() { Text = title, Height = height, Width = 736, Padding = new Padding(16, 30, 16, 12), Margin = new Padding(0, 0, 0, 14) };
        table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 0, Padding = new Padding(0, 2, 0, 0) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        Label introLabel = new() { Text = intro, Dock = DockStyle.Top, Height = 42, AutoEllipsis = true, Padding = new Padding(0, 0, 0, 4) };
        section.Controls.Add(table);
        section.Controls.Add(introLabel);
        return section;
    }

    private static void AddSetting(TableLayoutPanel table, string title, string description, Control editor, string? settingsValue = null, string? suffix = null)
    {
        if (settingsValue is not null && editor is TextBox textBox) textBox.Text = settingsValue;
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        Panel textPanel = new() { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 12, 4) };
        Label titleLabel = new() { Text = title, Dock = DockStyle.Top, Height = 24, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
        Label descriptionLabel = new() { Text = description, Dock = DockStyle.Fill, AutoEllipsis = true };
        textPanel.Controls.Add(descriptionLabel); textPanel.Controls.Add(titleLabel);
        Panel editorPanel = new() { Dock = DockStyle.Fill, Padding = new Padding(0, 16, 0, 0) };
        editor.Dock = DockStyle.Top;
        editorPanel.Controls.Add(editor);
        if (suffix is not null)
        {
            Label suffixLabel = new() { Text = suffix, Dock = DockStyle.Bottom, Height = 18, TextAlign = ContentAlignment.TopLeft };
            editorPanel.Controls.Add(suffixLabel);
        }
        table.Controls.Add(textPanel, 0, row); table.Controls.Add(editorPanel, 1, row);
    }

    private static void ConfigureNumber(NumericUpDown box, decimal minimum, decimal maximum, long value)
    {
        box.Minimum = minimum; box.Maximum = maximum; box.Value = Math.Clamp((decimal)value, minimum, maximum); box.Dock = DockStyle.Top;
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
        Text = "About DualCopy";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(500, 300);
        Padding = new Padding(26);

        Label title = new() { Text = "DualCopy", Font = new Font(SystemFonts.DefaultFont.FontFamily, 22, FontStyle.Bold), Dock = DockStyle.Top, Height = 42 };
        Label subtitle = new() { Text = "Private clipboard sharing between iPhone and Windows", Dock = DockStyle.Top, Height = 34 };
        Label details = new() { Text = "Version 0.3.1\n\nTransfers text, images, RAW camera files, and ordinary files over a trusted local TCP connection.\n\nAuthor: Ilyas Tekdin / DualCopy contributors", Dock = DockStyle.Top, Height = 112 };
        LinkLabel repository = new() { Text = "Open the DualCopy GitHub repository", AutoSize = true, Dock = DockStyle.Top, Height = 28 };
        repository.LinkClicked += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/EliasTekdin/DualCopy") { UseShellExecute = true });
        Button close = new() { Text = "Close", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 34 };
        Controls.Add(close); Controls.Add(repository); Controls.Add(details); Controls.Add(subtitle); Controls.Add(title);
        DualCopyAppearance.Apply(this, false);
    }
}
