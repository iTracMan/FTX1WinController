namespace FTX1WinController.Cat;

public enum CatErrorKind { Disconnected, Timeout, MalformedResponse }

public sealed class CatException : Exception
{
    public CatErrorKind Kind { get; }
    public CatException(CatErrorKind kind, string message) : base(message) => Kind = kind;
}

/// Serializes CAT command/response exchanges over one serial link. The
/// Yaesu CAT protocol is strictly half-duplex (one command in, one answer
/// out), so this keeps a FIFO of pending sends and only has one in flight
/// at a time.
///
/// Ported from the Mac app's CATConnection.swift, which is a Swift `actor`
/// (message-passing serializes access to its own state automatically).
/// There's no direct C# equivalent, so this class uses a plain lock around
/// state mutation plus a manually-pumped queue to get the same "exactly one
/// command in flight, FIFO order" guarantee.
public sealed class CatConnection
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromMilliseconds(1200);

    /// Small pacing gap before the next command goes out after a
    /// fire-and-forget Set command, since there's no reply to synchronize on.
    private static readonly TimeSpan FireAndForgetPacingGap = TimeSpan.FromMilliseconds(40);

    private sealed record PendingCommand(string Command, bool ExpectsReply, TaskCompletionSource<string> Completion);

    private readonly object _gate = new();
    private ISerialTransport? _transport;
    private readonly List<byte> _buffer = new();
    private readonly Queue<PendingCommand> _writeQueue = new();
    private TaskCompletionSource<string>? _waiter;
    private bool _isProcessing;
    private object? _currentToken;

    public bool IsConnected
    {
        get { lock (_gate) return _transport != null; }
    }

    public void Connect(ISerialTransport transport)
    {
        Disconnect();
        lock (_gate)
        {
            _transport = transport;
            _transport.DataReceived += OnDataReceived;
        }
    }

    public void Disconnect()
    {
        TaskCompletionSource<string>? waiterToFail;
        List<PendingCommand> queueToFail;
        lock (_gate)
        {
            if (_transport != null)
            {
                _transport.DataReceived -= OnDataReceived;
                _transport.Close();
                _transport = null;
            }
            _buffer.Clear();
            _currentToken = null;
            _isProcessing = false;
            waiterToFail = _waiter;
            _waiter = null;
            queueToFail = new List<PendingCommand>(_writeQueue);
            _writeQueue.Clear();
        }

        var disconnected = new CatException(CatErrorKind.Disconnected, "Not connected");
        waiterToFail?.TrySetException(disconnected);
        foreach (var pending in queueToFail)
            pending.Completion.TrySetException(disconnected);
    }

    /// - Parameter expectsReply: Per the CAT manual's own description, an
    ///   "Answer" is what a Read command gets back. Pass false for Set
    ///   commands (FA&lt;freq&gt;;, MD0&lt;code&gt;;, TX0;/TX1;) so this doesn't sit
    ///   waiting for an acknowledgment the radio never sends; pass true
    ///   (the default) for Read commands, which do always get an Answer frame back.
    public Task<string> SendAsync(string command, bool expectsReply = true)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_transport == null)
            {
                tcs.TrySetException(new CatException(CatErrorKind.Disconnected, "Not connected"));
                return tcs.Task;
            }
            _writeQueue.Enqueue(new PendingCommand(command, expectsReply, tcs));
        }
        Pump();
        return tcs.Task;
    }

    private void Pump()
    {
        PendingCommand pending;
        ISerialTransport transport;
        lock (_gate)
        {
            if (_isProcessing || _writeQueue.Count == 0) return;
            if (_transport == null)
            {
                pending = _writeQueue.Dequeue();
                pending.Completion.TrySetException(new CatException(CatErrorKind.Disconnected, "Not connected"));
                _ = Task.Run(Pump);
                return;
            }
            _isProcessing = true;
            pending = _writeQueue.Dequeue();
            transport = _transport;
        }

        try
        {
            transport.Write(pending.Command);
        }
        catch (Exception ex)
        {
            lock (_gate) { _isProcessing = false; }
            pending.Completion.TrySetException(ex);
            Pump();
            return;
        }

        if (!pending.ExpectsReply)
        {
            pending.Completion.TrySetResult("");
            _ = Task.Run(async () =>
            {
                await Task.Delay(FireAndForgetPacingGap);
                lock (_gate) { _isProcessing = false; }
                Pump();
            });
            return;
        }

        var token = new object();
        lock (_gate)
        {
            _waiter = pending.Completion;
            _currentToken = token;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(ResponseTimeout);
            TimeoutIfNeeded(token);
        });
    }

    private void TimeoutIfNeeded(object token)
    {
        TaskCompletionSource<string>? pendingWaiter;
        lock (_gate)
        {
            if (!ReferenceEquals(_currentToken, token) || _waiter == null) return;
            pendingWaiter = _waiter;
            _waiter = null;
            _currentToken = null;
            _isProcessing = false;
        }
        pendingWaiter.TrySetException(new CatException(CatErrorKind.Timeout, "Radio did not respond in time"));
        Pump();
    }

    private void OnDataReceived(byte[] bytes)
    {
        var toComplete = new List<(TaskCompletionSource<string> Waiter, string Frame)>();
        lock (_gate)
        {
            _buffer.AddRange(bytes);
            int idx;
            while ((idx = _buffer.IndexOf((byte)';')) >= 0)
            {
                var frameBytes = _buffer.GetRange(0, idx + 1).ToArray();
                _buffer.RemoveRange(0, idx + 1);
                if (_waiter != null)
                {
                    var frame = System.Text.Encoding.UTF8.GetString(frameBytes);
                    toComplete.Add((_waiter, frame));
                    _waiter = null;
                    _currentToken = null;
                    _isProcessing = false;
                }
                // Unsolicited frames would only appear if Auto Information
                // (AI) mode were enabled; this app never turns that on, so
                // any stray frame here (waiter already null) is dropped
                // rather than guessed at.
            }
        }
        foreach (var (waiter, frame) in toComplete)
            waiter.TrySetResult(frame);
        if (toComplete.Count > 0) Pump();
    }
}
