namespace FTX1WinController.Cat;

/// Abstraction over a physical serial connection so CatConnection's
/// queueing/timeout/framing logic can be built and unit-tested without a
/// real COM port. The Windows implementation (wrapping
/// System.IO.Ports.SerialPort, with the Mac app's confirmed 8N1/no-flow-
/// control settings and exclusive-open handling) is added once real
/// hardware is wired up.
public interface ISerialTransport
{
    bool IsOpen { get; }

    /// Raised for each chunk of bytes read from the port. Frame assembly
    /// (splitting on ';') is CatConnection's job, not the transport's — a
    /// chunk may contain zero, one, or several frames, or a partial one.
    event Action<byte[]>? DataReceived;

    void Write(string text);

    void Close();
}
