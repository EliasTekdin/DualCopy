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
    private readonly HistoryStore history;
    private readonly SemaphoreSlim writerGate = new(1, 1);
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
        connectionCancellation?.Cancel();
        try { listener?.Stop(); } catch { }
        connectionCancellation = new CancellationTokenSource();
        _ = ListenAsync(connectionCancellation.Token);
        _ = ConnectAsync(connectionCancellation.Token);
    }

    public async Task SendClipboardAsync(ClipboardEntry entry)
    {
        await SendAsync(new { type = "clipboard", id = entry.Id, kind = entry.Kind, text = entry.Text, data = entry.Data, timestamp = entry.Timestamp }).ConfigureAwait(false);
    }

    public async Task<string> TestConnectionAsync(string targetAddress, int targetPort)
    {
        if (string.IsNullOrWhiteSpace(targetAddress)) return "Connection test failed: enter an iPhone IP address.";
        try
        {
            using TcpClient client = new();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(targetAddress.Trim(), targetPort, timeout.Token).ConfigureAwait(false);
            return $"Connection test succeeded: {targetAddress.Trim()}:{targetPort}";
        }
        catch (Exception error)
        {
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
            StatusChanged?.Invoke($"Listening on port {port}");
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await server.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleConnectionAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException error)
        {
            StatusChanged?.Invoke($"Listen failed: {error.Message}");
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
                await HandleConnectionAsync(client, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                StatusChanged?.Invoke($"Outgoing connection: {error.Message}");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
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
                StatusChanged?.Invoke("Connected to DualCopy peer");
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
            }
            finally
            {
                RemoveWriter(writer);
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
                    List<ClipboardEntry>? received = historyElement.Deserialize<List<ClipboardEntry>>();
                    if (received is not null)
                    {
                        history.Merge(received);
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
            if (type == "clipboard")
            {
                ClipboardEntry? entry = root.Deserialize<ClipboardEntry>();
                if (entry is not null && history.Merge(new[] { entry }))
                    RemoteClipboardReceived?.Invoke(entry);
            }
        }
        catch (JsonException)
        {
            StatusChanged?.Invoke("Received an invalid clipboard message");
        }
    }

    private async Task SendAsync(object message)
    {
        StreamWriter[] active;
        lock (writersLock) active = writers.ToArray();
        foreach (StreamWriter writer in active)
            await SendToWriterAsync(writer, message, CancellationToken.None).ConfigureAwait(false);
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
            await writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken).ConfigureAwait(false);
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
