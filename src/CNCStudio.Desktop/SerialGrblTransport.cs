using System.IO.Ports;
using System.Text;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Serial port for GRBL: 115200 baud, 8 data bits, no parity, 1 stop bit (verified: config.h,
/// Commands wiki). Text is ASCII; real-time commands are single raw bytes.
/// </summary>
internal sealed class SerialGrblTransport : IGrblTransport
{
    private readonly SerialPort _port;

    public SerialGrblTransport(string portName)
    {
        _port = new SerialPort(portName, GrblProtocol.BaudRate, Parity.None, 8, StopBits.One)
        {
            Encoding = Encoding.ASCII,
            Handshake = Handshake.None,
            ReadTimeout = 500,
            WriteTimeout = 1000
        };
        _port.DataReceived += (_, _) =>
        {
            try { if (_port.IsOpen) DataReceived?.Invoke(_port.ReadExisting()); }
            catch (Exception) { /* port closed while reading */ }
        };
    }

    public event Action<string>? DataReceived;
    public bool IsOpen => _port.IsOpen;
    public void Open() => _port.Open();
    public void Close() { if (_port.IsOpen) _port.Close(); }
    public void Write(string text) => _port.Write(text);
    public void WriteByte(byte value) => _port.Write(new[] { value }, 0, 1);
    public void Dispose() { Close(); _port.Dispose(); }

    public static string[] AvailablePorts() =>
        SerialPort.GetPortNames().Distinct().OrderBy(p => p.Length).ThenBy(p => p).ToArray();
}
