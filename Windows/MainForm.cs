using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
        Color background = dark ? DarkBack : SystemColors.Control;
        Color surface = dark ? DarkControl : SystemColors.Window;
        Color text = dark ? DarkText : SystemColors.ControlText;

        control.BackColor = background;
        control.ForeColor = text;
        ApplySpecial(control, dark, surface, text);

        foreach (Control child in control.Controls)
        {
            child.ForeColor = text;
            ApplySpecial(child, dark, surface, text);
            Apply(child, dark);
        }
    }

    private static void ApplySpecial(Control control, bool dark, Color surface, Color text)
    {
        if (control is TextBoxBase or NumericUpDown or ComboBox)
        {
            control.BackColor = surface;
            control.ForeColor = text;
        }
        else if (control is Button or CheckBox)
        {
            control.BackColor = dark ? DarkControl : SystemColors.Control;
            control.ForeColor = text;
        }
        else if (control is Label or LinkLabel)
        {
            control.BackColor = dark ? DarkBack : SystemColors.Control;
            control.ForeColor = text;
        }
        else if (control is Form or Panel)
        {
            control.BackColor = dark ? DarkBack : SystemColors.Control;
            control.ForeColor = text;
        }

        if (control is ComboBox combo)
        {
            combo.FlatStyle = FlatStyle.Standard;
        }
        if (control is LinkLabel link)
        {
            link.LinkColor = dark ? Color.LightSkyBlue : SystemColors.HotTrack;
            link.ActiveLinkColor = dark ? Color.White : SystemColors.HotTrack;
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
        settings = DualCopySettings.Load(SettingsPath()).Normalized();
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

        Text = "DualCopy";
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        WindowState = FormWindowState.Minimized;
        Opacity = 0;
        Width = 1;
        Height = 1;
        Shown += (_, _) =>
        {
            Hide();
            if (settings.OpenInterfaceOnLaunch) ShowConfigure();
        };
        FormClosed += (_, _) => { trayIcon.Visible = false; trayIcon.Dispose(); peer.Dispose(); };

        clipboardTimer = new System.Windows.Forms.Timer { Interval = settings.PollingInterval };
        clipboardTimer.Tick += (_, _) => PollClipboard();
        clipboardTimer.Start();
        peer.Start();
    }

    private void ShowConfigure()
    {
        using ConfigureForm dialog = new(settings, lastStatus, (address, port) => peer.TestConnectionAsync(address, port));
        if (dialog.ShowDialog() != DialogResult.OK) return;
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
        dialog.ShowDialog();
    }

    private void ShowAbout()
    {
        using AboutForm dialog = new(settings.DarkMode);
        dialog.ShowDialog();
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

    private static string TrimStatus(string status) => status.Length > 55 ? status[..55] : status;
    private static string SettingsPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualCopy", "settings.json");

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
    private readonly CheckBox launchBox = new();
    private readonly Label statusLabel = new();
    private readonly Button testButton = new();
    private readonly Func<string, int, Task<string>> testConnection;
    private readonly DualCopySettings initial;

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
        DarkMode = darkBox.Checked,
        OpenInterfaceOnLaunch = launchBox.Checked
    };

    public ConfigureForm(DualCopySettings settings, string status, Func<string, int, Task<string>> testConnection)
    {
        initial = settings;
        this.testConnection = testConnection;
        Text = "DualCopy settings";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(590, 590);

        AddLabel("iPhone IP address", 16, 18); addressBox.SetBounds(220, 14, 340, 24); addressBox.Text = settings.Address; Controls.Add(addressBox);
        AddLabel("iPhone listening port", 16, 50); ConfigureNumber(portBox, 220, 46, 65535, settings.Port, 1); AddControl(portBox);
        AddLabel("Polling interval (ms)", 16, 82); ConfigureNumber(intervalBox, 220, 78, 5000, settings.PollingInterval, 100); AddControl(intervalBox);
        AddLabel("History limit", 16, 114); ConfigureNumber(historyBox, 220, 110, 1000, settings.HistoryLimit, 10); AddControl(historyBox);
        AddLabel("Max image bytes (0 = unlimited)", 16, 146); ConfigureNumber(imageBytesBox, 220, 142, int.MaxValue, settings.MaxImageBytes, 0); AddControl(imageBytesBox);
        AddLabel("Max file bytes (0 = unlimited)", 16, 178); ConfigureNumber(fileBytesBox, 220, 174, int.MaxValue, settings.MaxFileBytes, 0); AddControl(fileBytesBox);
        AddLabel("Image format", 16, 210); formatBox.SetBounds(220, 206, 120, 24); formatBox.DropDownStyle = ComboBoxStyle.DropDownList; formatBox.Items.AddRange(new object[] { "auto", "png", "jpeg" }); formatBox.SelectedItem = settings.ImageFormat; if (formatBox.SelectedIndex < 0) formatBox.SelectedIndex = 0; Controls.Add(formatBox);
        AddLabel("JPEG quality", 16, 242); qualityBox.SetBounds(220, 238, 120, 24); qualityBox.DecimalPlaces = 2; qualityBox.Increment = 0.01M; qualityBox.Minimum = 0.40M; qualityBox.Maximum = 1.00M; qualityBox.Value = (decimal)Math.Clamp(settings.JpegQuality, 0.40f, 1.0f); Controls.Add(qualityBox);
        AddLabel("Receive folder", 16, 274); folderBox.SetBounds(220, 270, 340, 24); folderBox.Text = settings.ReceiveFolder; Controls.Add(folderBox);

        imagesBox.Text = "Sync images"; imagesBox.Checked = settings.SyncImages; imagesBox.SetBounds(16, 312, 180, 24); Controls.Add(imagesBox);
        filesBox.Text = "Sync files"; filesBox.Checked = settings.SyncFiles; filesBox.SetBounds(220, 312, 180, 24); Controls.Add(filesBox);
        rawBox.Text = "Convert raw images when possible"; rawBox.Checked = settings.ConvertRawImages; rawBox.SetBounds(16, 344, 260, 24); Controls.Add(rawBox);
        darkBox.Text = "Dark mode"; darkBox.Checked = settings.DarkMode; darkBox.SetBounds(220, 344, 150, 24); Controls.Add(darkBox);
        launchBox.Text = "Open interface on startup"; launchBox.Checked = settings.OpenInterfaceOnLaunch; launchBox.SetBounds(380, 344, 190, 24); Controls.Add(launchBox);

        statusLabel.SetBounds(16, 388, 544, 38); statusLabel.AutoEllipsis = true; statusLabel.Text = "Status: " + status; Controls.Add(statusLabel);
        testButton.Text = "Test connection"; testButton.SetBounds(16, 440, 130, 30); testButton.Click += async (_, _) =>
        {
            testButton.Enabled = false;
            statusLabel.Text = "Status: testing connection…";
            statusLabel.Text = "Status: " + await this.testConnection(addressBox.Text.Trim(), (int)portBox.Value);
            testButton.Enabled = true;
        }; Controls.Add(testButton);
        Button save = new() { Text = "Save", DialogResult = DialogResult.OK, Left = 400, Top = 530, Width = 75 };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 485, Top = 530, Width = 75 };
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
        list.Dock = DockStyle.Fill; list.View = View.Details; list.FullRowSelect = true; list.HideSelection = false; list.Columns.Add("Type", 90); list.Columns.Add("Name / preview", 470); list.Columns.Add("Time", 120); Controls.Add(list);
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
    public AboutForm(bool dark)
    {
        Text = "About DualCopy"; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(390, 210); Padding = new Padding(18);
        Label text = new() { Dock = DockStyle.Fill, Text = "DualCopy\n\nVersion 0.3.3\nShare text, images, and files between iPhone and Windows.\n\nAuthor: Ilyas Tekdin / DualCopy contributors\nGitHub: github.com/EliasTekdin/DualCopy", AutoSize = false };
        Controls.Add(text); Button close = new() { Text = "Close", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 30 }; Controls.Add(close);
        DualCopyAppearance.Apply(this, dark);
    }
}
