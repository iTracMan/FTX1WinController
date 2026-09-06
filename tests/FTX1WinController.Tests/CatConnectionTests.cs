using FTX1WinController.Cat;

namespace FTX1WinController.Tests;

/// Regression tests for CatConnection's half-duplex queueing/timeout/
/// framing behavior — ported from the Mac app's actor-based CATConnection,
/// exercised here through the fake ISerialTransport instead of any real port.
public class CatConnectionTests
{
    [Fact]
    public async Task SendAsync_WritesCommandAndReturnsTheAnswerFrame()
    {
        var transport = new FakeSerialTransport();
        var connection = new CatConnection();
        connection.Connect(transport);

        var task = connection.SendAsync("FA;");
        transport.SimulateReceive("FA014250000;");
        var result = await task;

        Assert.Equal("FA014250000;", result);
        Assert.Equal(new[] { "FA;" }, transport.WrittenCommands);
    }

    [Fact]
    public async Task SendAsync_FireAndForget_CompletesWithoutWaitingForAReply()
    {
        var transport = new FakeSerialTransport();
        var connection = new CatConnection();
        connection.Connect(transport);

        var result = await connection.SendAsync("TX1;", expectsReply: false);

        Assert.Equal("", result);
        Assert.Equal(new[] { "TX1;" }, transport.WrittenCommands);
    }

    [Fact]
    public async Task SendAsync_QueuesCommands_OnlyOneInFlightAtATime()
    {
        var transport = new FakeSerialTransport();
        var connection = new CatConnection();
        connection.Connect(transport);

        var first = connection.SendAsync("FA;");
        var second = connection.SendAsync("MD0;");

        // The second command must not reach the wire until the first's
        // Answer arrives — half-duplex, exactly one outstanding command.
        Assert.Equal(new[] { "FA;" }, transport.WrittenCommands);

        transport.SimulateReceive("FA014250000;");
        Assert.Equal("FA014250000;", await first);

        // Answering the first unblocks the queue, which the pump drains synchronously.
        Assert.Equal(new[] { "FA;", "MD0;" }, transport.WrittenCommands);

        transport.SimulateReceive("MD02;");
        Assert.Equal("MD02;", await second);
    }

    [Fact]
    public async Task OnDataReceived_AssemblesAFrameSplitAcrossMultipleChunks()
    {
        var transport = new FakeSerialTransport();
        var connection = new CatConnection();
        connection.Connect(transport);

        var task = connection.SendAsync("FA;");
        transport.SimulateReceive("FA0142");
        transport.SimulateReceive("50000;");

        Assert.Equal("FA014250000;", await task);
    }

    [Fact]
    public async Task Disconnect_FailsAnyPendingCommandsAsDisconnected()
    {
        var transport = new FakeSerialTransport();
        var connection = new CatConnection();
        connection.Connect(transport);

        var task = connection.SendAsync("FA;");
        connection.Disconnect();

        var ex = await Assert.ThrowsAsync<CatException>(() => task);
        Assert.Equal(CatErrorKind.Disconnected, ex.Kind);
    }

    [Fact]
    public void Connect_SubscribesBeforeOpening_AndPropagatesOpenFailures()
    {
        var transport = new FakeSerialTransport { ThrowOnOpen = true };
        var connection = new CatConnection();

        // Mirrors WindowsSerialTransport surfacing an already-open COM port
        // as UnauthorizedAccessException — Connect() must let that escape
        // rather than swallow it, so a caller (RadioController.ConnectAsync)
        // can show a dedicated "port busy" message.
        Assert.Throws<UnauthorizedAccessException>(() => connection.Connect(transport));
        Assert.False(connection.IsConnected);
    }

    [Fact]
    public async Task SendAsync_WhenNotConnected_FailsImmediately()
    {
        var connection = new CatConnection();

        var ex = await Assert.ThrowsAsync<CatException>(() => connection.SendAsync("FA;"));
        Assert.Equal(CatErrorKind.Disconnected, ex.Kind);
    }

    [Fact]
    public async Task SendAsync_TimesOutWhenNoAnswerArrives()
    {
        var transport = new FakeSerialTransport();
        var connection = new CatConnection();
        connection.Connect(transport);

        var task = connection.SendAsync("FA;");

        var ex = await Assert.ThrowsAsync<CatException>(() => task);
        Assert.Equal(CatErrorKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task FireAndForget_PacingGapDelaysTheNextCommandBeforeItIsWritten()
    {
        var transport = new FakeSerialTransport();
        var connection = new CatConnection();
        connection.Connect(transport);

        await connection.SendAsync("TX1;", expectsReply: false);
        var next = connection.SendAsync("FA;");

        // Immediately after the fire-and-forget command completes, the
        // ~40ms pacing gap means the next command should not be on the wire yet.
        Assert.Equal(new[] { "TX1;" }, transport.WrittenCommands);

        // Poll for the pacing gap to elapse rather than a fixed sleep, to
        // keep this from being flaky under load while still bounding the wait.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (transport.WrittenCommands.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(new[] { "TX1;", "FA;" }, transport.WrittenCommands);

        transport.SimulateReceive("FA014250000;");
        Assert.Equal("FA014250000;", await next);
    }
}
