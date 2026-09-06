using FTX1WinController.Cat;

namespace FTX1WinController.Tests;

/// A minimal in-memory ISerialTransport for driving CatConnection in tests
/// without any real COM port — exactly the reason CatConnection is written
/// against this interface rather than System.IO.Ports.SerialPort directly.
public sealed class FakeSerialTransport : ISerialTransport
{
    public bool IsOpen { get; private set; } = true;
    public event Action<byte[]>? DataReceived;
    public List<string> WrittenCommands { get; } = new();
    public bool ThrowOnWrite { get; set; }

    public void Write(string text)
    {
        if (ThrowOnWrite) throw new IOException("simulated write failure");
        WrittenCommands.Add(text);
    }

    public void Close() => IsOpen = false;

    public void SimulateReceive(string text) => DataReceived?.Invoke(System.Text.Encoding.UTF8.GetBytes(text));
}
