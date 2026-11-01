using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace DualCopy.Windows;

internal sealed class MainForm : Form
{
    private readonly HistoryStore history = new();
    private readonly NotifyIcon trayIcon;
    private readonly System.Windows.Forms.Timer clipboardTimer;
    private readonly SynchronizationContext uiContext;
    private PeerClient peer;
    private string? lastClipboard;
    private byte[]? lastImageData;
    private bool applyingRemote;
    private string phoneAddress;
    private int phonePort;

    public MainForm()
    {
        (phoneAddress, phonePort) = LoadSettings();
        uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        peer = new PeerClient(history, phoneAddress, phonePort);
        peer.StatusChanged += status => uiContext.Post(_ => trayIcon.Text = "DualCopy: " + TrimStatus(status), null);
        peer.RemoteClipboardReceived += entry => uiContext.Post(_ => ApplyRemoteClipboard(entry), null);

        trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "DualCopy: connecting"
        };
        ContextMenuStrip menu = new();
        menu.Items.Add("Configure", null, (_, _) => ShowConfigure());
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

        clipboardTimer = new System.Windows.Forms.Timer { Interval = 400 };
        clipboardTimer.Tick += (_, _) => PollClipboard();
        clipboardTimer.Start();
        peer.Start();
    }

    private void PollClipboard()
    {
        if (applyingRemote) return;
        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text) && text != lastClipboard)
                {
                    lastClipboard = text;
                    lastImageData = null;
                    _ = peer.SendClipboardAsync(history.AddText(text));
                    return;
                }
            }
            if (Clipboard.ContainsImage())
            {
                byte[]? data = ImageToPng(Clipboard.GetImage());
                if (data is { Length: > 0 } && !data.AsSpan().SequenceEqual(lastImageData ?? Array.Empty<byte>()))
                {
                    lastImageData = data;
                    _ = peer.SendClipboardAsync(history.AddImage(Convert.ToBase64String(data)));
                }
            }
        }
        catch (ExternalException)
        {
            // Another process owns the clipboard; the next timer tick retries.
        }
    }

    private void ApplyRemoteClipboard(ClipboardEntry entry)
    {
        try
        {
            applyingRemote = true;
            if (entry.Kind == "image" && !string.IsNullOrEmpty(entry.Data))
            {
                byte[] data = Convert.FromBase64String(entry.Data);
                using MemoryStream stream = new(data);
                using Image source = Image.FromStream(stream);
                using Bitmap bitmap = new(source);
                Clipboard.SetImage(bitmap);
                lastImageData = data;
                lastClipboard = null;
            }
            else if (entry.Kind == "text" && !string.IsNullOrEmpty(entry.Text))
            {
                Clipboard.SetText(entry.Text);
                lastClipboard = entry.Text;
                lastImageData = null;
            }
        }
        catch (ExternalException)
        {
            trayIcon.Text = "DualCopy: could not write clipboard";
        }
        catch (FormatException)
        {
            trayIcon.Text = "DualCopy: invalid image received";
        }
        finally
        {
            applyingRemote = false;
        }
    }

    private static byte[]? ImageToPng(Image? image)
    {
        if (image is null) return null;
        using (image)
        using (MemoryStream stream = new())
        {
            image.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
    }

    private void ShowConfigure()
    {
        using ConfigureForm dialog = new(phoneAddress, phonePort);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        phoneAddress = dialog.PhoneAddress;
        phonePort = dialog.PhonePort;
        SaveSettings(phoneAddress, phonePort);
        peer.Configure(phoneAddress, phonePort);
        peer.Start();
    }

    private static string TrimStatus(string status) => status.Length > 55 ? status[..55] : status;

    private static (string Address, int Port) LoadSettings()
    {
        try
        {
            string path = SettingsPath();
            if (File.Exists(path))
            {
                Settings? settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
                if (settings is not null && settings.Port is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(settings.Address))
                    return (settings.Address, settings.Port);
            }
        }
        catch { }
        return ("192.168.1.1", 49732);
    }

    private static void SaveSettings(string address, int port)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath())!);
        File.WriteAllText(SettingsPath(), JsonSerializer.Serialize(new Settings(address, port)));
    }

    private static string SettingsPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualCopy", "settings.json");
    private sealed record Settings(string Address, int Port);
}

internal sealed class ConfigureForm : Form
{
    private readonly TextBox addressBox = new();
    private readonly NumericUpDown portBox = new();
    public string PhoneAddress => addressBox.Text.Trim();
    public int PhonePort => (int)portBox.Value;

    public ConfigureForm(string address, int port)
    {
        Text = "DualCopy connection";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(360, 140);

        Controls.Add(new Label { Text = "iPhone IP address", Left = 16, Top = 18, AutoSize = true });
        addressBox.SetBounds(145, 14, 195, 24);
        addressBox.Text = address;
        Controls.Add(addressBox);
        Controls.Add(new Label { Text = "TCP port", Left = 16, Top = 54, AutoSize = true });
        portBox.SetBounds(145, 50, 100, 24);
        portBox.Minimum = 1;
        portBox.Maximum = 65535;
        portBox.Value = Math.Clamp(port, 1, 65535);
        Controls.Add(portBox);

        Button save = new() { Text = "Save", DialogResult = DialogResult.OK, Left = 180, Top = 96, Width = 75 };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 260, Top = 96, Width = 75 };
        Controls.Add(save);
        Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
    }
}
