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
    public static readonly Color LightBack = Color.FromArgb(244, 244, 242);
    public static readonly Color LightSurface = Color.White;
    public static readonly Color LightText = Color.FromArgb(32, 34, 36);
    public static readonly Color DarkBack = Color.FromArgb(25, 26, 28);
    public static readonly Color DarkSurface = Color.FromArgb(36, 38, 42);
    public static readonly Color DarkText = Color.FromArgb(242, 242, 240);
    public static readonly Color LightBorder = Color.FromArgb(211, 211, 207);
    public static readonly Color DarkBorder = Color.FromArgb(76, 79, 84);

    public static void Apply(Control control, bool dark)
    {
        Color background = dark ? DarkBack : LightBack;
        Color surface = dark ? DarkSurface : LightSurface;
        Color text = dark ? DarkText : LightText;
        Color input = dark ? Color.FromArgb(48, 50, 55) : Color.White;
        Color buttonSurface = dark ? Color.FromArgb(57, 60, 65) : Color.FromArgb(235, 235, 231);
        Color border = dark ? DarkBorder : LightBorder;

        control.BackColor = control switch
        {
            TextBoxBase or NumericUpDown or ComboBox => input,
            Button => buttonSurface,
            TabPage or GroupBox or Panel or TableLayoutPanel or FlowLayoutPanel => surface,
            _ => background
        };
        control.ForeColor = text;

        if (control is GroupBox group) group.ForeColor = text;
        if (control is LinkLabel link)
        {
            link.LinkColor = text;
            link.ActiveLinkColor = dark ? Color.White : Color.Black;
        }

        foreach (Control child in control.Controls)
        {
            child.ForeColor = text;
            child.BackColor = child switch
            {
                TextBoxBase or NumericUpDown or ComboBox => input,
                Button => buttonSurface,
                TabPage or GroupBox or Panel or TableLayoutPanel or FlowLayoutPanel => surface,
                _ => child is Label or LinkLabel ? surface : background
            };
            if (child is TextBoxBase textBox)
            {
                textBox.BorderStyle = BorderStyle.FixedSingle;
                textBox.BackColor = input;
                textBox.ForeColor = text;
            }
            else if (child is NumericUpDown number)
            {
                number.BackColor = input;
                number.ForeColor = text;
            }
            else if (child is ComboBox combo)
            {
                combo.BackColor = input;
                combo.ForeColor = text;
                combo.FlatStyle = FlatStyle.Flat;
            }
            else if (child is Button button)
            {
                button.BackColor = buttonSurface;
                button.ForeColor = text;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = border;
            }
            else if (child is LinkLabel childLink)
            {
                childLink.LinkColor = text;
                childLink.ActiveLinkColor = dark ? Color.White : Color.Black;
            }
            Apply(child, dark);
        }
    }
}

internal sealed class MainForm : Form
{
    private readonly HistoryStore history = new();
    private NotifyIcon trayIcon = null!;
    private readonly System.Windows.Forms.Timer clipboardTimer;
    private readonly SynchronizationContext uiContext;
    private readonly TabControl mainTabs = new();
    private readonly Label connectionStatus = new();
    private readonly Label configureStatus = new();
    private readonly ListView historyList = new();
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
    private PeerClient peer;
    private DualCopySettings settings;
    private string? lastClipboard;
    private string? lastImageFingerprint;
    private string? lastFileFingerprint;
    private bool applyingRemote;
    private bool exitRequested;
    private string lastStatus = "Starting…";

    public MainForm()
    {
        settings = DualCopySettings.Load(SettingsPath()).Normalized();
        history.Limit = settings.HistoryLimit;
        uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        peer = new PeerClient(history, settings.Address, settings.Port);

        Text = "DualCopy";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 640);
        ClientSize = new Size(1080, 760);
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = true;

        BuildInterface();
        ConfigureTray();
        ApplyTheme();

        peer.StatusChanged += status => uiContext.Post(_ =>
        {
            lastStatus = status;
            connectionStatus.Text = "●  " + status;
            trayIcon.Text = "DualCopy: " + TrimStatus(status);
        }, null);
        peer.RemoteClipboardReceived += entry => uiContext.Post(_ => ApplyRemoteClipboard(entry), null);

        FormClosing += (_, args) =>
        {
            SaveConfiguration(false);
            if (!exitRequested)
            {
                args.Cancel = true;
                Hide();
            }
        };
        FormClosed += (_, _) =>
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
            peer.Dispose();
        };
        Shown += (_, _) =>
        {
            if (settings.OpenInterfaceOnLaunch) ShowInterface(0);
            else Hide();
        };

        clipboardTimer = new System.Windows.Forms.Timer { Interval = settings.PollingInterval };
        clipboardTimer.Tick += (_, _) => PollClipboard();
        clipboardTimer.Start();
        peer.Start();
    }

    private void BuildInterface()
    {
        Panel header = new() { Dock = DockStyle.Top, Height = 104, Padding = new Padding(30, 22, 30, 16) };
        Label title = new() { Text = "DualCopy", AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, 22, FontStyle.Bold), Location = new Point(30, 20) };
        Label subtitle = new() { Text = "Private clipboard sharing between your iPhone and Windows computer", AutoSize = true, Location = new Point(32, 60) };
        connectionStatus.Text = "●  " + lastStatus;
        connectionStatus.AutoSize = true;
        connectionStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        connectionStatus.Location = new Point(790, 38);
        header.Controls.Add(title); header.Controls.Add(subtitle); header.Controls.Add(connectionStatus);

        mainTabs.Dock = DockStyle.Fill;
        mainTabs.Padding = new Point(18, 8);
        mainTabs.SelectedIndexChanged += (_, _) =>
        {
            if (mainTabs.SelectedIndex == 1) RefreshHistoryPage();
        };
        mainTabs.TabPages.Add(BuildConfigurePage());
        mainTabs.TabPages.Add(BuildHistoryPage());
        mainTabs.TabPages.Add(BuildAboutPage());

        Controls.Add(mainTabs);
        Controls.Add(header);
    }

    private void ConfigureTray()
    {
        trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "DualCopy: connecting"
        };
        ContextMenuStrip menu = new();
        menu.Items.Add("Configure", null, (_, _) => ShowInterface(0));
        menu.Items.Add("Clipboard history", null, (_, _) => ShowInterface(1));
        menu.Items.Add("About DualCopy", null, (_, _) => ShowInterface(2));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        trayIcon.ContextMenuStrip = menu;
        trayIcon.DoubleClick += (_, _) => ShowInterface(0);
    }

    private TabPage BuildConfigurePage()
    {
        TabPage page = NewTab("Configure");
        FlowLayoutPanel flow = NewFlow(1000);

        flow.Controls.Add(CreateSection("Connection", "Choose the iPhone address and TCP port used by DualCopy. Both devices must be on a network that allows them to reach each other.", out TableLayoutPanel connection));
        addressBox.Text = settings.Address;
        AddSetting(connection, "iPhone IP address", "The iPhone’s local LAN or VPN IPv4 address. Windows uses this address when it initiates a connection.", addressBox);
        ConfigureNumber(portBox, 1, 65535, settings.Port);
        AddSetting(connection, "iPhone listening port", "The port where the iPhone accepts DualCopy traffic. It must match the iPhone’s Listen port in Settings. The default is 49732.", portBox, "TCP port");

        flow.Controls.Add(CreateSection("Content synchronisation", "Select the types of clipboard content to exchange. Limits protect the computer from unexpected memory and disk usage; set a limit to 0 when you explicitly want unlimited Windows-side handling.", out TableLayoutPanel content));
        imagesBox.Text = "Synchronise images"; imagesBox.Checked = settings.SyncImages;
        AddSetting(content, "Images", "When enabled, copied pictures travel in both directions. Disabling it reduces memory, battery, and network use.", imagesBox);
        rawBox.Text = "Convert RAW images when possible"; rawBox.Checked = settings.ConvertRawImages;
        AddSetting(content, "RAW conversion", "Attempts to turn DNG and other RAW camera data into a normal image. Conversion is best effort; if decoding fails, the original file is retained.", rawBox);
        formatBox.DropDownStyle = ComboBoxStyle.DropDownList;
        formatBox.Items.AddRange(new object[] { "auto", "png", "jpeg" });
        formatBox.SelectedItem = settings.ImageFormat;
        if (formatBox.SelectedIndex < 0) formatBox.SelectedIndex = 0;
        AddSetting(content, "Output image format", "Auto chooses a compatible representation. PNG preserves lossless detail; JPEG is smaller but lossy. This also determines the format used when a RAW file can be converted.", formatBox);
        qualityBox.DecimalPlaces = 2; qualityBox.Increment = 0.01M; qualityBox.Minimum = 0.40M; qualityBox.Maximum = 1.00M; qualityBox.Value = (decimal)Math.Clamp(settings.JpegQuality, 0.40f, 1.0f);
        AddSetting(content, "JPEG quality (0.40–1.00)", "This is a quality fraction, not a file-size percentage. 0.40 creates smaller files with more artefacts; 0.82 is the balanced default; 1.00 preserves the most detail but usually creates larger files. PNG ignores this value.", qualityBox);
        ConfigureNumber(imageBytesBox, 0, int.MaxValue, settings.MaxImageBytes);
        AddSetting(content, "Maximum image size", "The largest encoded image DualCopy will send from Windows. 0 means unlimited on Windows. A limit reduces freezes and memory pressure, but an image above it is skipped.", imageBytesBox, "bytes; 0 = unlimited");
        filesBox.Text = "Synchronise files"; filesBox.Checked = settings.SyncFiles;
        AddSetting(content, "Files", "When enabled, copied files travel in both directions. This includes TXT, PDF, ZIP, DEB, and DNG when the source application exposes them as files.", filesBox);
        ConfigureNumber(fileBytesBox, 0, int.MaxValue, settings.MaxFileBytes);
        AddSetting(content, "Maximum file size", "The largest file DualCopy will read and transmit from Windows. 0 means unlimited on Windows, but very large files still consume RAM and may be refused by the iPhone limit.", fileBytesBox, "bytes; 0 = unlimited");
        folderBox.Text = settings.ReceiveFolder;
        AddSetting(content, "Receive folder", "Windows writes files received from the iPhone here. The folder is created automatically when the first file arrives.", folderBox);

        flow.Controls.Add(CreateSection("Performance and history", "These settings balance detection speed, retained clipboard entries, CPU use, memory, and disk space.", out TableLayoutPanel performance));
        ConfigureNumber(intervalBox, 100, 5000, settings.PollingInterval);
        AddSetting(performance, "Polling interval", "How often Windows checks for a new clipboard item. 500 ms feels responsive; lower values add CPU use, while higher values save resources but add delay.", intervalBox, "milliseconds");
        ConfigureNumber(historyBox, 10, 1000, settings.HistoryLimit);
        AddSetting(performance, "History limit", "How many recent entries DualCopy retains for synchronisation and the Clipboard History tab. More entries use more storage and memory.", historyBox, "entries");

        flow.Controls.Add(CreateSection("Appearance and startup", "Use a neutral light or dark appearance and decide whether launching the executable should immediately show the window or keep DualCopy in the notification area.", out TableLayoutPanel appearance));
        darkBox.Text = "Use dark mode"; darkBox.Checked = settings.DarkMode;
        darkBox.CheckedChanged += (_, _) => ApplyTheme();
        AddSetting(appearance, "Theme", "Light mode uses a clean white and grey workspace. Dark mode uses charcoal surfaces and high-contrast text. No clipboard behaviour changes.", darkBox);
        launchBox.Text = "Open the interface when DualCopy starts"; launchBox.Checked = settings.OpenInterfaceOnLaunch;
        AddSetting(appearance, "Startup behaviour", "When enabled, double-clicking DualCopy.exe opens the Configure tab immediately. When disabled, DualCopy starts quietly in the notification area; double-click the tray icon whenever you want the interface.", launchBox);

        Panel actions = new() { Width = 960, Height = 58, Padding = new Padding(2, 10, 2, 4), Margin = new Padding(0, 0, 0, 18) };
        Button test = new() { Text = "Test connection", Width = 130, Height = 32, Location = new Point(2, 12) };
        test.Click += async (_, _) =>
        {
            test.Enabled = false;
            configureStatus.Text = "Status: testing connection…";
            configureStatus.Text = "Status: " + await peer.TestConnectionAsync(addressBox.Text.Trim(), (int)portBox.Value);
            test.Enabled = true;
        };
        Button save = new() { Text = "Save changes", Width = 120, Height = 32, Location = new Point(142, 12) };
        save.Click += (_, _) => SaveConfiguration(true);
        configureStatus.Text = "Status: " + lastStatus;
        configureStatus.AutoEllipsis = true;
        configureStatus.Location = new Point(280, 18);
        configureStatus.Size = new Size(650, 24);
        actions.Controls.Add(test); actions.Controls.Add(save); actions.Controls.Add(configureStatus);
        flow.Controls.Add(actions);
        page.Controls.Add(flow);
        return page;
    }

    private TabPage BuildHistoryPage()
    {
        TabPage page = NewTab("Clipboard History");
        Panel content = new() { Dock = DockStyle.Fill, Padding = new Padding(24) };
        Label heading = new() { Text = "Clipboard History", Font = new Font(SystemFonts.DefaultFont.FontFamily, 18, FontStyle.Bold), Dock = DockStyle.Top, Height = 36 };
        Label description = new() { Text = "Review recent text, images, and files retained by DualCopy. Selecting an entry and choosing Copy places it back on the Windows clipboard.", Dock = DockStyle.Top, Height = 42 };
        historyList.Dock = DockStyle.Fill;
        historyList.View = View.Details;
        historyList.FullRowSelect = true;
        historyList.HideSelection = false;
        historyList.GridLines = true;
        historyList.Columns.Clear();
        historyList.Columns.Add("Type", 100);
        historyList.Columns.Add("Name or preview", 560);
        historyList.Columns.Add("Time", 170);
        FlowLayoutPanel actions = new() { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft };
        Button close = new() { Text = "Hide window", Width = 100 };
        close.Click += (_, _) => Hide();
        Button clear = new() { Text = "Clear history", Width = 110 };
        clear.Click += (_, _) => { history.Clear(); RefreshHistoryPage(); };
        Button copy = new() { Text = "Copy selected", Width = 110 };
        copy.Click += (_, _) => CopySelectedHistory();
        actions.Controls.Add(close); actions.Controls.Add(clear); actions.Controls.Add(copy);
        content.Controls.Add(historyList); content.Controls.Add(actions); content.Controls.Add(description); content.Controls.Add(heading);
        page.Controls.Add(content);
        RefreshHistoryPage();
        return page;
    }

    private TabPage BuildAboutPage()
    {
        TabPage page = NewTab("About DualCopy");
        Panel content = new() { Dock = DockStyle.Fill, Padding = new Padding(42, 36, 42, 24) };
        Label title = new() { Text = "About DualCopy", Font = new Font(SystemFonts.DefaultFont.FontFamily, 24, FontStyle.Bold), Dock = DockStyle.Top, Height = 48 };
        Label subtitle = new() { Text = "Private clipboard sharing between iPhone and Windows", Dock = DockStyle.Top, Height = 34 };
        Label details = new() { Text = "Version 0.3.2\n\nTransfers text, images, RAW camera files, and ordinary files over a trusted local TCP connection.\n\nAuthor: Ilyas Tekdin / DualCopy contributors", AutoSize = false, Dock = DockStyle.Top, Height = 150, Padding = new Padding(0, 18, 0, 0) };
        LinkLabel repository = new() { Text = "Open the DualCopy GitHub repository", AutoSize = true, Dock = DockStyle.Top, Height = 30 };
        repository.LinkClicked += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/EliasTekdin/DualCopy") { UseShellExecute = true });
        Label support = new() { Text = "The Windows companion runs in the notification area when its window is hidden. Double-click the tray icon to return here.", AutoSize = false, Dock = DockStyle.Top, Height = 60, Padding = new Padding(0, 18, 0, 0) };
        content.Controls.Add(support); content.Controls.Add(repository); content.Controls.Add(details); content.Controls.Add(subtitle); content.Controls.Add(title);
        page.Controls.Add(content);
        return page;
    }

    private void SaveConfiguration(bool showStatus)
    {
        if (addressBox is null) return;
        settings = (settings with
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
        }).Normalized();
        settings.Save(SettingsPath());
        history.Limit = settings.HistoryLimit;
        clipboardTimer.Interval = settings.PollingInterval;
        peer.Configure(settings.Address, settings.Port);
        peer.Start();
        if (showStatus) configureStatus.Text = "Status: settings saved";
    }

    private void ApplyTheme() => DualCopyAppearance.Apply(this, darkBox.Checked);

    private void ShowInterface(int tabIndex)
    {
        mainTabs.SelectedIndex = Math.Clamp(tabIndex, 0, mainTabs.TabPages.Count - 1);
        ShowInTaskbar = true;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
        if (mainTabs.SelectedIndex == 1) RefreshHistoryPage();
    }

    private void ExitApplication()
    {
        exitRequested = true;
        Close();
    }

    private void RefreshHistoryPage()
    {
        historyList.BeginUpdate();
        historyList.Items.Clear();
        foreach (ClipboardEntry entry in history.Snapshot())
        {
            string value = entry.Kind == "text" ? entry.Text ?? "" : entry.Name ?? entry.Kind;
            if (value.Length > 100) value = value[..100] + "…";
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
                using MemoryStream stream = new(Convert.FromBase64String(entry.Data));
                using Image source = Image.FromStream(stream);
                using Bitmap bitmap = new(source);
                Clipboard.SetImage(bitmap);
            }
            else if (entry.Kind == "file" && entry.Data is not null)
            {
                Directory.CreateDirectory(settings.ReceiveFolder);
                string path = Path.Combine(settings.ReceiveFolder, Path.GetFileName(entry.Name ?? "DualCopy-file.bin"));
                File.WriteAllBytes(path, Convert.FromBase64String(entry.Data));
                Clipboard.SetFileDropList(new StringCollection { path });
            }
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "DualCopy", MessageBoxButtons.OK, MessageBoxIcon.Error); }
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
                    lastFileFingerprint = fingerprint; lastClipboard = null; lastImageFingerprint = null;
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
                    lastImageFingerprint = fingerprint; lastFileFingerprint = null; lastClipboard = null;
                    _ = peer.SendClipboardAsync(history.AddImage(Convert.ToBase64String(data)));
                }
                return;
            }
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text) && text != lastClipboard)
                {
                    lastClipboard = text; lastImageFingerprint = null; lastFileFingerprint = null;
                    _ = peer.SendClipboardAsync(history.AddText(text));
                }
            }
        }
        catch (ExternalException) { }
        catch (IOException) { trayIcon.Text = "DualCopy: could not read a file"; }
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
                using MemoryStream stream = new(data); using Image source = Image.FromStream(stream); using Bitmap bitmap = new(source);
                Clipboard.SetImage(bitmap); lastImageFingerprint = ImageFingerprint(bitmap); lastFileFingerprint = null; lastClipboard = null;
            }
            else if (entry.Kind == "file" && !string.IsNullOrEmpty(entry.Data))
            {
                if (!settings.SyncFiles) return;
                byte[] data = Convert.FromBase64String(entry.Data);
                if (settings.MaxFileBytes > 0 && data.Length > settings.MaxFileBytes) return;
                string path = SaveReceivedFile(entry.Name, data);
                Clipboard.SetFileDropList(new StringCollection { path });
                lastFileFingerprint = FileFingerprint(new[] { new FilePayload(Path.GetFileName(path), data, MimeForPath(path)) }); lastImageFingerprint = null; lastClipboard = null;
                trayIcon.Text = "DualCopy: received " + TrimStatus(Path.GetFileName(path));
            }
            else if (entry.Kind == "text" && !string.IsNullOrEmpty(entry.Text))
            {
                Clipboard.SetText(entry.Text); lastClipboard = entry.Text; lastImageFingerprint = null; lastFileFingerprint = null;
            }
        }
        catch (ExternalException) { trayIcon.Text = "DualCopy: could not write clipboard"; }
        catch (FormatException) { trayIcon.Text = "DualCopy: invalid data received"; }
        catch (ArgumentException) { trayIcon.Text = "DualCopy: unsupported image or file"; }
        finally { applyingRemote = false; }
    }

    private string SaveReceivedFile(string? requestedName, byte[] data)
    {
        Directory.CreateDirectory(settings.ReceiveFolder);
        string safeName = Path.GetFileName(string.IsNullOrWhiteSpace(requestedName) ? "DualCopy-file.bin" : requestedName);
        if (string.IsNullOrWhiteSpace(safeName) || safeName is "." or "..") safeName = "DualCopy-file.bin";
        string path = Path.Combine(settings.ReceiveFolder, safeName);
        if (File.Exists(path))
        {
            string stem = Path.GetFileNameWithoutExtension(safeName); string extension = Path.GetExtension(safeName);
            path = Path.Combine(settings.ReceiveFolder, $"{stem}-{DateTime.Now:yyyyMMdd-HHmmssfff}{extension}");
        }
        if (settings.ConvertRawImages && Path.GetExtension(path).Equals(".dng", StringComparison.OrdinalIgnoreCase) && TryConvertImage(data, settings, out byte[] converted))
        {
            path = Path.ChangeExtension(path, settings.ImageFormat == "jpeg" ? ".jpg" : ".png"); data = converted;
        }
        File.WriteAllBytes(path, data); return path;
    }

    private List<FilePayload> ReadFilePayloads(StringCollection paths)
    {
        List<FilePayload> result = new();
        foreach (string path in paths.Cast<string>().Take(20))
        {
            if (!File.Exists(path)) continue;
            FileInfo info = new(path);
            if (settings.MaxFileBytes > 0 && info.Length > settings.MaxFileBytes) continue;
            byte[] data = File.ReadAllBytes(path); string name = Path.GetFileName(path);
            if (settings.ConvertRawImages && Path.GetExtension(path).Equals(".dng", StringComparison.OrdinalIgnoreCase) && TryConvertImage(data, settings, out byte[] converted))
            {
                data = converted; name = Path.ChangeExtension(name, settings.ImageFormat == "jpeg" ? ".jpg" : ".png");
            }
            result.Add(new FilePayload(name, data, MimeForPath(name)));
        }
        return result;
    }

    private static string FileFingerprint(IEnumerable<FilePayload> files)
    {
        using SHA256 sha = SHA256.Create(); using MemoryStream stream = new();
        foreach (FilePayload file in files) { byte[] name = System.Text.Encoding.UTF8.GetBytes(file.Name); stream.Write(name); stream.Write(file.Data); }
        return Convert.ToHexString(sha.ComputeHash(stream.ToArray()));
    }

    private static string? ImageFingerprint(Image? image)
    {
        if (image is null || image.Width <= 0 || image.Height <= 0) return null;
        using Bitmap normalized = new(image.Width, image.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(normalized)) { graphics.Clear(Color.Transparent); graphics.DrawImage(image, new Rectangle(0, 0, normalized.Width, normalized.Height)); }
        using MemoryStream stream = new(); normalized.Save(stream, ImageFormat.Png);
        return $"{normalized.Width}x{normalized.Height}:{Convert.ToHexString(SHA256.HashData(stream.ToArray()))}";
    }

    private static byte[]? ImageToData(Image? image, DualCopySettings options)
    {
        if (image is null) return null;
        using MemoryStream stream = new(); ImageFormat format = options.ImageFormat == "jpeg" ? ImageFormat.Jpeg : ImageFormat.Png;
        if (format == ImageFormat.Jpeg) SaveAsJpeg(image, stream, options.JpegQuality); else image.Save(stream, format);
        byte[] data = stream.ToArray();
        if (options.MaxImageBytes == 0 || data.Length <= options.MaxImageBytes) return data;
        if (options.ImageFormat == "auto") { stream.SetLength(0); SaveAsJpeg(image, stream, options.JpegQuality); data = stream.ToArray(); if (data.Length <= options.MaxImageBytes) return data; }
        return null;
    }

    private static void SaveAsJpeg(Image image, Stream output, float quality)
    {
        ImageCodecInfo? codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(item => item.FormatID == ImageFormat.Jpeg.Guid);
        if (codec is null) { image.Save(output, ImageFormat.Jpeg); return; }
        using EncoderParameters parameters = new(1); parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Round(Math.Clamp(quality, 0.40f, 1.0f) * 100)); image.Save(output, codec, parameters);
    }

    private static bool TryConvertImage(byte[] source, DualCopySettings options, out byte[] converted)
    {
        converted = Array.Empty<byte>();
        try { using MemoryStream input = new(source); using Image image = Image.FromStream(input); byte[]? result = ImageToData(image, options); if (result is null) return false; converted = result; return true; }
        catch (ArgumentException) { return false; }
        catch (ExternalException) { return false; }
    }

    private static string MimeForPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".pdf" => "application/pdf", ".zip" => "application/zip", ".txt" => "text/plain", ".dng" => "image/x-adobe-dng", _ => "application/octet-stream"
    };

    private static string TrimStatus(string status) => status.Length > 55 ? status[..55] : status;
    private static string SettingsPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualCopy", "settings.json");
    private static TabPage NewTab(string name) => new() { Text = name, Padding = new Padding(14), AutoScroll = true, BackColor = DualCopyAppearance.LightBack };
    private static FlowLayoutPanel NewFlow(int width) => new() { Dock = DockStyle.Top, Width = width, AutoSize = true, AutoScroll = false, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(4) };

    private static GroupBox CreateSection(string title, string intro, out TableLayoutPanel table)
    {
        GroupBox section = new() { Text = title, Width = 970, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 28, 16, 14), Margin = new Padding(0, 0, 0, 16) };
        table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 2, 0, 0) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        Label introLabel = new() { Text = intro, AutoSize = true, MaximumSize = new Size(900, 0), Padding = new Padding(0, 0, 0, 12) };
        table.Controls.Add(introLabel, 0, 0); table.SetColumnSpan(introLabel, 2); table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        section.Controls.Add(table);
        return section;
    }

    private static void AddSetting(TableLayoutPanel table, string title, string description, Control editor, string? suffix = null)
    {
        int row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Panel textPanel = new() { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 5, 16, 6) };
        Label titleLabel = new() { Text = title, AutoSize = true, Dock = DockStyle.Top, Font = new Font(SystemFonts.DefaultFont.FontFamily, SystemFonts.DefaultFont.Size, FontStyle.Bold) };
        Label descriptionLabel = new() { Text = description, AutoSize = true, MaximumSize = new Size(600, 0), Dock = DockStyle.Top };
        textPanel.Controls.Add(descriptionLabel); textPanel.Controls.Add(titleLabel);
        Panel editorPanel = new() { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 14, 0, 6), MinimumSize = new Size(220, 44) };
        editor.Dock = DockStyle.Top; editorPanel.Controls.Add(editor);
        if (suffix is not null) { Label suffixLabel = new() { Text = suffix, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 4, 0, 0) }; editorPanel.Controls.Add(suffixLabel); }
        table.Controls.Add(textPanel, 0, row); table.Controls.Add(editorPanel, 1, row);
    }

    private static void ConfigureNumber(NumericUpDown box, decimal minimum, decimal maximum, long value)
    {
        box.Minimum = minimum; box.Maximum = maximum; box.Value = Math.Clamp((decimal)value, minimum, maximum); box.Dock = DockStyle.Top;
    }

    private sealed record FilePayload(string Name, byte[] Data, string Mime);
}
