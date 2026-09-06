using System.IO.Ports;
using System.Text;

namespace FTX1WinController.Cat;

/// ISerialTransport backed by System.IO.Ports.SerialPort — the FTX-1's two
/// virtual COM ports (CAT-1 enhanced for frequency/mode, CAT-2 standard for
/// PTT), 38400 baud, confirmed on the Mac app's own SerialPort.swift as
/// 8 data bits / no parity / 1 stop bit / no flow control.
///
/// Subscribes to the underlying SerialPort's own DataReceived event in the
/// constructor, before Open() is ever called — .NET can't raise that event
/// on a port that isn't open yet, so there's no window for it to fire
/// before something is listening, unlike the race the Mac app's
/// SerialPort.swift had to guard against explicitly for its GCD read source.
public sealed class WindowsSerialTransport : ISerialTransport
{
    private readonly SerialPort _port;

    public bool IsOpen => _port.IsOpen;
    public event Action<byte[]>? DataReceived;

    public WindowsSerialTransport(string portName, int baudRate)
    {
        _port = new SerialPort(portName, baudRate)
        {
            Parity = Parity.None,
            DataBits = 8,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            Encoding = Encoding.ASCII,
        };
        _port.DataReceived += OnPortDataReceived;
    }

    /// Throws System.UnauthorizedAccessException if the port is already
    /// held open elsewhere (another process, or another handle in this
    /// one) — the Windows analog of the Mac app's TIOCEXCL/EBUSY case.
    public void Open() => _port.Open();

    public void Write(string text)
    {
        if (!_port.IsOpen) throw new InvalidOperationException("Serial port is not open.");
        _port.Write(text);
    }

    public void Close()
    {
        _port.DataReceived -= OnPortDataReceived;
        try
        {
            if (_port.IsOpen) _port.Close();
        }
        finally
        {
            _port.Dispose();
        }
    }

    private void OnPortDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var available = _port.BytesToRead;
            if (available <= 0) return;
            var buffer = new byte[available];
            var read = _port.Read(buffer, 0, available);
            if (read <= 0) return;
            DataReceived?.Invoke(read == buffer.Length ? buffer : buffer[..read]);
        }
        catch (Exception)
        {
            // Nowhere useful to propagate a read failure to from this
            // event-handler callback — CatConnection's own 1.2s response
            // timeout is what surfaces a stuck command to the caller instead.
        }
    }
}
