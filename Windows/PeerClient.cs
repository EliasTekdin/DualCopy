using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DualCopy.Windows;

internal sealed class PeerClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HistoryStore history;
    private readonly SemaphoreSlim writerGate = new(1, 1);
    private readonly object writersLock = new();
    private readonly List<StreamWriter> writers = new();
    private CancellationTokenSource? connectionCancellation;
    private TcpListener? listener;
    private string address;
    private int port;
    private string? lastConnectFailure;

    public event Action<ClipboardEntry>? RemoteClipboardReceived;
    public event Action<string>? StatusChanged;

    public PeerClient(HistoryStore history, string address, int port)
    {
        this.history = history;
        this.address = address;
        this.port = port;
    }

    public void Configure(string newAddress, int newPort)
    {
        address = newAddress;
        port = newPort;
    }

    public void Start()
    {
        connectionCancellation?.Cancel();
        try { listener?.Stop(); } catch { }
        connectionCancellation = new CancellationTokenSource();
        DualCopyLog.Info($"Starting Windows peer: listenPort={port}, target={address}:{port}.");
        _ = ListenAsync(connectionCancellation.Token);
        _ = ConnectAsync(connectionCancellation.Token);
    }

    public async Task SendClipboardAsync(ClipboardEntry entry)
    {
        await SendAsync(new
        {
            type = "clipboard",
            id = entry.Id,
            kind = entry.Kind,
            text = entry.Text,
            data = entry.Data,
            timestamp = entry.Timestamp,
            name = entry.Name,
            uti = entry.Uti,
            mime = entry.Mime
        }).ConfigureAwait(false);
    }

    public async Task<string> TestConnectionAsync(string targetAddress, int targetPort)
    {
        if (string.IsNullOrWhiteSpace(targetAddress))
        {
            DualCopyLog.Warn("Connection test could not start: no iPhone IP address was entered.");
            return "Connection test failed: enter an iPhone IP address.";
        }
        try
        {
            using TcpClient client = new();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(targetAddress.Trim(), targetPort, timeout.Token).ConfigureAwait(false);
            string success = $"Connection test succeeded: {targetAddress.Trim()}:{targetPort}";
            DualCopyLog.Info(success + ".");
            return success;
        }
        catch (Exception error)
        {
            DualCopyLog.Warn($"Connection test failed for {targetAddress.Trim()}:{targetPort}.", error);
            return $"Connection test failed: {error.Message}";
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        TcpListener server = new(IPAddress.Any, port);
        listener = server;
        try
        {
            server.Start();
            DualCopyLog.Info($"Windows TCP listener is active on port {port}.");
            StatusChanged?.Invoke($"Listening on port {port}");
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await server.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                ConfigureTcpKeepAlive(client);
                DualCopyLog.Info($"Accepted incoming TCP peer from {client.Client.RemoteEndPoint}.");
                _ = HandleConnectionAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException error)
        {
            StatusChanged?.Invoke($"Listen failed: {error.Message}");
            DualCopyLog.Error($"Windows TCP listener failed on port {port}.", error);
        }
        catch (Exception error)
        {
            StatusChanged?.Invoke($"Listen failed: {error.Message}");
            DualCopyLog.Error("Unexpected failure in the Windows TCP listener.", error);
        }
        finally
        {
            try { server.Stop(); } catch { }
            if (ReferenceEquals(listener, server)) listener = null;
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using TcpClient client = new();
                await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
                ConfigureTcpKeepAlive(client);
                lastConnectFailure = null;
                DualCopyLog.Info($"Connected from Windows to iPhone at {address}:{port}.");
                await HandleConnectionAsync(client, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                StatusChanged?.Invoke($"Outgoing connection: {error.Message}");
                string failure = $"{address}:{port}: {error.GetType().Name}: {error.Message}";
                if (!string.Equals(lastConnectFailure, failure, StringComparison.Ordinal))
                {
                    lastConnectFailure = failure;
                    DualCopyLog.Warn($"Windows outgoing connection to iPhone failed for {address}:{port}.", error);
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static void ConfigureTcpKeepAlive(TcpClient client)
    {
        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            byte[] keepAliveValues = new byte[12];
            BitConverter.GetBytes((uint)1).CopyTo(keepAliveValues, 0);
            BitConverter.GetBytes((uint)30_000).CopyTo(keepAliveValues, 4);
            BitConverter.GetBytes((uint)10_000).CopyTo(keepAliveValues, 8);
            client.Client.IOControl(IOControlCode.KeepAliveValues, keepAliveValues, null);
            DualCopyLog.Info("TCP keepalive enabled for peer socket (idle=30 s interval=10 s).");
        }
        catch (Exception error)
        {
            // Keepalive tuning is best-effort; a platform refusal must not stop clipboard networking.
            DualCopyLog.Warn("Could not configure TCP keepalive for a peer socket.", error);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (NetworkStream stream = client.GetStream())
        using (StreamReader reader = new(stream))
        using (StreamWriter writer = new(stream) { AutoFlush = true })
        {
            AddWriter(writer);
            try
            {
                string remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                StatusChanged?.Invoke("Connected to DualCopy peer");
                DualCopyLog.Info($"Peer session started with {remote}; sending Windows hello/history.");
                ClipboardEntry? before = history.Newest;
                await SendToWriterAsync(writer, new { type = "hello", device = "windows", history = history.Snapshot() }, cancellationToken).ConfigureAwait(false);

                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
                {
                    await HandleMessageAsync(line, writer, before, cancellationToken).ConfigureAwait(false);
                    before = history.Newest;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                StatusChanged?.Invoke($"Peer disconnected: {error.Message}");
                DualCopyLog.Warn("Peer session ended with a transport or protocol error.", error);
            }
            finally
            {
                RemoveWriter(writer);
                DualCopyLog.Info("Peer session closed.");
            }
        }
    }

    private async Task HandleMessageAsync(string line, StreamWriter writer, ClipboardEntry? before, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string? type = root.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() : null;
            if (type is "hello" or "history")
            {
                string property = type == "hello" ? "history" : "entries";
                if (root.TryGetProperty(property, out JsonElement historyElement))
                {
                    List<ClipboardEntry>? received = historyElement.Deserialize<List<ClipboardEntry>>(JsonOptions);
                    if (received is not null)
                    {
                        bool merged = history.Merge(received);
                        DualCopyLog.Info($"Received {type} with {received.Count} history item(s); new entries merged={merged}.");
                        if (type == "hello")
                        {
                            await SendToWriterAsync(writer, new { type = "history", entries = history.Snapshot() }, cancellationToken).ConfigureAwait(false);
                            ClipboardEntry? newest = history.Newest;
                            if (newest is not null && (before is null || newest.Timestamp > before.Timestamp))
                                RemoteClipboardReceived?.Invoke(newest);
                        }
                    }
                }
                return;
            }
            if (type == "diagnostic")
            {
                LogRemoteDiagnostic(root);
                return;
            }
            if (type == "clipboard")
            {
                ClipboardEntry? entry = root.Deserialize<ClipboardEntry>(JsonOptions);
                if (entry is not null && history.Merge(new[] { entry }))
                {
                    DualCopyLog.Info($"Received new remote clipboard entry: kind={entry.Kind}, textChars={entry.Text?.Length ?? 0}, dataBase64Chars={entry.Data?.Length ?? 0}.");
                    RemoteClipboardReceived?.Invoke(entry);
                }
                else
                {
                    DualCopyLog.Warn("Received a clipboard message that was invalid or already present in Windows history.");
                }
                return;
            }
            DualCopyLog.Warn($"Received unsupported peer message type '{type ?? "missing"}'.");
        }
        catch (JsonException error)
        {
            StatusChanged?.Invoke("Received an invalid clipboard message");
            DualCopyLog.Warn("Could not parse a peer JSON message.", error);
        }
    }

    private static void LogRemoteDiagnostic(JsonElement root)
    {
        string device = ReadDiagnosticText(root, "device", 20);
        if (!string.Equals(device, "iphone", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(device, "ios", StringComparison.OrdinalIgnoreCase))
        {
            DualCopyLog.Warn("Ignored a peer diagnostic with an unrecognised source device.");
            return;
        }

        string eventName = ReadDiagnosticText(root, "event", 80);
        if (eventName.Length == 0 || !IsSafeDiagnosticEvent(eventName)) eventName = "unknown";
        string message = ReadDiagnosticText(root, "message", 400);
        string details = $"iPhone diagnostic [{eventName}]: {message}";
        if (TryReadNonNegativeInteger(root, "imageBytes", out long imageBytes))
            details += $" imageBytes={imageBytes}";
        if (TryReadNonNegativeInteger(root, "maxImageBytes", out long maxImageBytes))
            details += $" maxImageBytes={maxImageBytes}";
        if (TryReadNonNegativeInteger(root, "base64Chars", out long base64Chars))
            details += $" base64Chars={base64Chars}";

        string level = ReadDiagnosticText(root, "level", 12);
        if (string.Equals(level, "error", StringComparison.OrdinalIgnoreCase))
            DualCopyLog.Error(details);
        else if (string.Equals(level, "warn", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(level, "warning", StringComparison.OrdinalIgnoreCase))
            DualCopyLog.Warn(details);
        else
            DualCopyLog.Info(details);
    }

    private static string ReadDiagnosticText(JsonElement root, string property, int maximumLength)
    {
        if (!root.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            return string.Empty;
        string text = (value.GetString() ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > maximumLength ? text[..maximumLength] : text;
    }

    private static bool IsSafeDiagnosticEvent(string value)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-')
                return false;
        }
        return true;
    }

    private static bool TryReadNonNegativeInteger(JsonElement root, string property, out long number)
    {
        number = 0;
        return root.TryGetProperty(property, out JsonElement value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt64(out number) && number >= 0;
    }

    private async Task SendAsync(object message)
    {
        StreamWriter[] active;
        lock (writersLock) active = writers.ToArray();
        if (active.Length == 0)
        {
            DualCopyLog.Warn("A local clipboard update could not be sent because there is no connected peer writer.");
            return;
        }

        int sent = 0;
        foreach (StreamWriter writer in active)
        {
            try
            {
                using CancellationTokenSource writeTimeout = new(TimeSpan.FromSeconds(15));
                await SendToWriterAsync(writer, message, writeTimeout.Token).ConfigureAwait(false);
                sent++;
            }
            catch (Exception error)
            {
                RemoveWriter(writer);
                try { writer.Dispose(); } catch { }
                DualCopyLog.Error("Failed writing a clipboard update to a connected peer.", error);
            }
        }
        DualCopyLog.Info($"Clipboard update sent to {sent} connected peer writer(s).");
    }

    private async Task SendToWriterAsync(StreamWriter writer, object message, CancellationToken cancellationToken)
    {
        lock (writersLock)
        {
            if (!writers.Contains(writer)) return;
        }
        await writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (writersLock)
            {
                if (!writers.Contains(writer)) return;
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(message, JsonOptions).AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writerGate.Release();
        }
    }

    private void AddWriter(StreamWriter writer)
    {
        lock (writersLock) writers.Add(writer);
    }

    private void RemoveWriter(StreamWriter writer)
    {
        lock (writersLock) writers.Remove(writer);
    }

    public void Dispose()
    {
        connectionCancellation?.Cancel();
        try { listener?.Stop(); } catch { }
        lock (writersLock)
        {
            foreach (StreamWriter writer in writers.ToArray())
            {
                try { writer.Dispose(); } catch { }
            }
            writers.Clear();
        }
        writerGate.Dispose();
    }
}
