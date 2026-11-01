using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DualCopy.Windows;

internal sealed class PeerClient : IDisposable
{
    private readonly HistoryStore history;
    private readonly SemaphoreSlim writerGate = new(1, 1);
    private CancellationTokenSource? connectionCancellation;
    private string address;
    private int port;

    public event Action<string>? RemoteClipboardReceived;
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
        connectionCancellation = new CancellationTokenSource();
        _ = RunAsync(connectionCancellation.Token);
    }

    public async Task SendClipboardAsync(ClipboardEntry entry)
    {
        await SendAsync(new { type = "clipboard", id = entry.Id, text = entry.Text, timestamp = entry.Timestamp }).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using TcpClient client = new();
                await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
                using NetworkStream stream = client.GetStream();
                using StreamReader reader = new(stream);
                using StreamWriter writer = new(stream) { AutoFlush = true };
                activeWriter = writer;
                StatusChanged?.Invoke($"Connected to {address}:{port}");
                ClipboardEntry? before = history.Newest;
                await WriteAsync(writer, new { type = "hello", device = "windows", history = history.Snapshot() }, cancellationToken).ConfigureAwait(false);

                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
                {
                    HandleMessage(line, writer, before, cancellationToken);
                    before = history.Newest;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                StatusChanged?.Invoke($"Disconnected: {error.Message}");
            }
            finally
            {
                activeWriter = null;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private StreamWriter? activeWriter;

    private void HandleMessage(string line, StreamWriter writer, ClipboardEntry? before, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string? type = root.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() : null;
            if (type is "hello" or "history")
            {
                if (root.TryGetProperty(type == "hello" ? "history" : "entries", out JsonElement historyElement))
                {
                    List<ClipboardEntry>? received = historyElement.Deserialize<List<ClipboardEntry>>();
                    if (received is not null)
                    {
                        history.Merge(received);
                        if (type == "hello")
                        {
                            _ = WriteAsync(writer, new { type = "history", entries = history.Snapshot() }, cancellationToken);
                            ClipboardEntry? newest = history.Newest;
                            if (newest is not null && (before is null || newest.Timestamp > before.Timestamp))
                                RemoteClipboardReceived?.Invoke(newest.Text);
                        }
                    }
                }
                return;
            }
            if (type == "clipboard")
            {
                ClipboardEntry? entry = root.Deserialize<ClipboardEntry>();
                if (entry is not null && history.Merge(new[] { entry }))
                    RemoteClipboardReceived?.Invoke(entry.Text);
            }
        }
        catch (JsonException)
        {
            StatusChanged?.Invoke("Received an invalid clipboard message");
        }
    }

    private async Task SendAsync(object message)
    {
        StreamWriter? writer = activeWriter;
        if (writer is null) return;
        await writerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (activeWriter == writer)
                await WriteAsync(writer, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The receive loop reports the disconnect and reconnects.
        }
        finally
        {
            writerGate.Release();
        }
    }

    private static async Task WriteAsync(StreamWriter writer, object message, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        connectionCancellation?.Cancel();
        writerGate.Dispose();
    }
}
