using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
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
    private readonly ConditionalWeakTable<StreamWriter, SemaphoreSlim> writerGates = new();
    private readonly object writersLock = new();
    private readonly List<StreamWriter> writers = new();
    private CancellationTokenSource? connectionCancellation;
    private TcpListener? listener;
    private string address;
    private int port;

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
        bool replacingSession = connectionCancellation is not null;
        connectionCancellation?.Cancel();
        try { listener?.Stop(); } catch { }
        connectionCancellation = new CancellationTokenSource();
        DualCopyLog.Info($"Starting Windows peer: listenPort={port}, target={address}:{port}, reason={(replacingSession ? "reconfigure/restart" : "initial startup")}.");
        _ = ListenAsync(connectionCancellation.Token);
        if (string.IsNullOrWhiteSpace(address))
        {
            DualCopyLog.Warn("Outbound connection is not starting because the iPhone address is not configured.");
            StatusChanged?.Invoke("Setup needed: enter the iPhone IP address");
        }
        else
        {
            _ = ConnectAsync(connectionCancellation.Token);
        }
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
        TimeSpan retryDelay = TimeSpan.FromSeconds(2);
        int consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            bool connected = false;
            try
            {
                using TcpClient client = new();
                await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
                ConfigureTcpKeepAlive(client);
                consecutiveFailures = 0;
                connected = true;
                retryDelay = TimeSpan.FromSeconds(2);
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
                consecutiveFailures++;
                DualCopyLog.Warn($"Windows outgoing connection attempt #{consecutiveFailures} to iPhone {address}:{port} failed; retrying in {retryDelay.TotalSeconds:0} s.", error);
            }

            TimeSpan delay = connected ? TimeSpan.FromSeconds(2) : retryDelay;
            try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (!connected) retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 60));
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
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        using (client)
        using (NetworkStream stream = client.GetStream())
        using (StreamReader reader = new(stream))
        using (StreamWriter writer = new(stream) { AutoFlush = true })
        {
            AddWriter(writer);
            try
            {
                StatusChanged?.Invoke("Connected to DualCopy peer");
                DualCopyLog.Info($"Peer session opened: remote={remote}, local={client.Client.LocalEndPoint}; sending hello with {history.Snapshot().Count} history item(s).");
                ClipboardEntry? before = history.Newest;
                await SendToWriterAsync(writer, new { type = "hello", device = "windows", history = history.Snapshot() }, cancellationToken).ConfigureAwait(false);

                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
                {
                    await HandleMessageAsync(line, writer, before, cancellationToken).ConfigureAwait(false);
                    before = history.Newest;
                }
                DualCopyLog.Warn($"Peer closed its TCP send side cleanly: remote={remote}; received EOF while waiting for the next JSON line.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DualCopyLog.Info($"Peer session stopped locally: remote={remote}, reason=peer restart, settings reconfigure, or application exit.");
            }
            catch (Exception error)
            {
                StatusChanged?.Invoke($"Peer disconnected: {error.Message}");
                DualCopyLog.Warn($"Peer session ended with a transport/protocol failure: remote={remote}.", error);
            }
            finally
            {
                RemoveWriter(writer);
                DualCopyLog.Info($"Peer session closed: remote={remote}.");
            }
        }
    }

    private async Task HandleMessageAsync(string line, StreamWriter writer, ClipboardEntry? before, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string? type = root.TryGetProperty("type", out JsonElement typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;
            DualCopyLog.Info($"Peer JSON received: type={type ?? "missing"}, payloadBytes={Encoding.UTF8.GetByteCount(line)}.");
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
        int queuedTimeouts = 0;
        foreach (StreamWriter writer in active)
        {
            try
            {
                using CancellationTokenSource writeTimeout = new(TimeSpan.FromSeconds(15));
                bool completed = await SendToWriterAsync(writer, message, writeTimeout.Token, preserveWriterOnQueueTimeout: true).ConfigureAwait(false);
                if (completed) sent++;
                else queuedTimeouts++;
            }
            catch (Exception error)
            {
                RemoveWriter(writer);
                try { writer.Dispose(); } catch { }
                DualCopyLog.Error("Clipboard send failed after entering the peer writer; the stream was closed as unsafe to reuse.", error);
            }
        }
        DualCopyLog.Info($"Clipboard update delivery summary: connectedWriters={active.Length}, sent={sent}, queueTimeouts={queuedTimeouts}.");
    }

    private async Task<bool> SendToWriterAsync(StreamWriter writer, object message, CancellationToken cancellationToken, bool preserveWriterOnQueueTimeout = false)
    {
        lock (writersLock)
        {
            if (!writers.Contains(writer)) return false;
        }

        string json = JsonSerializer.Serialize(message, JsonOptions);
        int payloadBytes = Encoding.UTF8.GetByteCount(json) + 1;
        string messageType = "unknown";
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("type", out JsonElement typeElement) && typeElement.ValueKind == JsonValueKind.String)
                messageType = typeElement.GetString() ?? "unknown";
        }
        catch (JsonException) { }

        SemaphoreSlim peerGate = writerGates.GetValue(writer, _ => new SemaphoreSlim(1, 1));
        long queuedAt = Stopwatch.GetTimestamp();
        try
        {
            await peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (preserveWriterOnQueueTimeout && cancellationToken.IsCancellationRequested)
        {
            DualCopyLog.Warn($"Clipboard send skipped before socket write: peer writer queue wait exceeded 15 s; type={messageType}, payloadBytes={payloadBytes}, waitMs={Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds:0}. Connection kept open.");
            return false;
        }

        try
        {
            lock (writersLock)
            {
                if (!writers.Contains(writer)) return false;
            }
            double queueWaitMs = Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
            long writeAt = Stopwatch.GetTimestamp();
            DualCopyLog.Info($"Peer JSON send started: type={messageType}, payloadBytes={payloadBytes}, queueWaitMs={queueWaitMs:0}.");
            try
            {
                await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DualCopyLog.Error($"Peer socket write was canceled after writer lock acquisition: type={messageType}, payloadBytes={payloadBytes}, writeMs={Stopwatch.GetElapsedTime(writeAt).TotalMilliseconds:0}; this stream will be closed.");
                throw;
            }
            DualCopyLog.Info($"Peer JSON send completed: type={messageType}, payloadBytes={payloadBytes}, writeMs={Stopwatch.GetElapsedTime(writeAt).TotalMilliseconds:0}.");
            return true;
        }
        finally
        {
            peerGate.Release();
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
    }
}
