using System.IO;
using System.Net.Sockets;
using System.Text;

namespace FTX1WinController.Bridge;

/// Client for the Mac-side FTX1Bridge's line-based text protocol (see the
/// FTX1Bridge Xcode target's NetworkBridge.swift for the exact command
/// grammar this mirrors — CONNECT/DISCONNECT/PORTS/STATUS/GET/SET, one
/// newline-terminated command per line, one newline-terminated response
/// back). This is the ONLY networking/protocol code in the whole app — no
/// CAT command logic lives here at all, the bridge owns every quirk.
public sealed class BridgeClient : IAsyncDisposable
{
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsOpen => _client?.Connected == true;

    public async Task OpenAsync(string host, int port)
    {
        await CloseAsync();
        var client = new TcpClient();
        await client.ConnectAsync(host, port);
        var stream = client.GetStream();
        _client = client;
        _reader = new StreamReader(stream, Encoding.ASCII);
        _writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\n" };
    }

    public Task CloseAsync()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _client?.Close();
        _reader = null;
        _writer = null;
        _client = null;
        return Task.CompletedTask;
    }

    /// Sends one command line and waits for exactly one response line —
    /// same strict one-at-a-time shape the bridge itself uses talking to
    /// the radio. Guarded with a semaphore since the poll loop and button
    /// clicks can both want to use this connection concurrently.
    public async Task<string> SendAsync(string command)
    {
        if (_writer is null || _reader is null)
            throw new InvalidOperationException("Not connected to the bridge.");

        await _gate.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(command);
            var line = await _reader.ReadLineAsync();
            return line ?? throw new IOException("Bridge closed the connection.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}
