using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using SatTrack.Core.Rotator;

namespace SatTrack.Core.Radio;

/// <summary>
/// FlexRadio control through a SmartSDR CAT serial port (the virtual COM ports created by the
/// SmartSDR CAT application, or a Flex radio's own CAT port). Uses the Kenwood-style CAT
/// commands Flex supports: "FA;" reads slice A's frequency, "FA00014074000;" sets it
/// (11 digits, Hz). Unknown or rejected commands are answered with "?;".
/// Also works with other Kenwood-protocol radios.
/// </summary>
public sealed class FlexCatRadio : IRadio
{
    private readonly SerialPort _port;
    private readonly object _lock = new();
    private static readonly Regex FreqReply = new(@"FA(\d{11});", RegexOptions.Compiled);

    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long to listen for a "?;" error after a set command (which has no reply).</summary>
    public TimeSpan SettleTime { get; set; } = TimeSpan.FromMilliseconds(40);

    public CommLog? Log { get; set; }

    public string PortName => _port.PortName;

    public string Description => $"FlexRadio CAT on {_port.PortName}";

    public bool IsOpen => _port.IsOpen;

    public FlexCatRadio(string portName, int baudRate = 9600)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            Encoding = Encoding.ASCII,
            ReadTimeout = 200,
            WriteTimeout = 1000,
            // SmartSDR CAT can map DTR/RTS to PTT or CW on some port types: keep them low.
            DtrEnable = false,
            RtsEnable = false,
        };
    }

    public void Open()
    {
        lock (_lock)
        {
            if (_port.IsOpen) return;
            Log?.Info($"Opening {_port.PortName} (SmartSDR CAT) at {_port.BaudRate} baud, 8N1, DTR/RTS off");
            try
            {
                _port.Open();
            }
            catch (Exception ex)
            {
                Log?.Error($"Couldn't open {_port.PortName}: {ex.Message}");
                throw;
            }
            Thread.Sleep(50);
            DrainStale();
            Log?.Info($"{_port.PortName} open");

            // Identify the radio for the log (not required for operation).
            try
            {
                string id = Query("ID;", new Regex(@"ID\d{3};"), routine: false);
                Log?.Info($"Radio identifies as {id.Trim()}");
            }
            catch
            {
                // Some CAT ports don't answer ID; the frequency read during connect is the real check.
            }
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            if (_port.IsOpen)
            {
                _port.Close();
                Log?.Info($"{_port.PortName} closed");
            }
        }
    }

    public void Dispose()
    {
        Close();
        _port.Dispose();
    }

    public long GetFrequencyHz()
    {
        lock (_lock)
        {
            string reply = Query("FA;", FreqReply, routine: true);
            return long.Parse(FreqReply.Match(reply).Groups[1].Value, CultureInfo.InvariantCulture);
        }
    }

    public void SetFrequencyHz(long hz)
    {
        if (hz < 0 || hz > 99_999_999_999) throw new ArgumentOutOfRangeException(nameof(hz));
        lock (_lock)
        {
            EnsureOpen();
            DrainStale();
            string cmd = $"FA{hz:00000000000};";
            Log?.Tx($"{cmd}   [set frequency {hz / 1e6:0.000000} MHz]");
            _port.Write(cmd);

            string reply = ReadFor(SettleTime);
            if (reply.Length > 0) Log?.Rx(reply);
            if (reply.Contains("?;"))
            {
                Log?.Error("Radio rejected the frequency command");
                throw new CivException("The radio rejected the command (?;). The frequency may be outside its range.");
            }
        }
    }

    private string Query(string command, Regex expected, bool routine)
    {
        EnsureOpen();
        DrainStale();
        Log?.Tx(command, routine);
        _port.Write(command);

        var buffer = new StringBuilder();
        var deadline = DateTime.UtcNow + ResponseTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_port.BytesToRead == 0) { Thread.Sleep(5); continue; }
            buffer.Append(_port.ReadExisting());
            string text = buffer.ToString();

            var m = expected.Match(text);
            if (m.Success)
            {
                Log?.Rx(text, routine);
                return m.Value;
            }
            if (text.Contains("?;"))
            {
                Log?.Rx(text);
                Log?.Error($"Radio rejected '{command}'");
                throw new CivException($"The radio rejected '{command}'.");
            }
        }

        if (buffer.Length > 0) Log?.Rx(buffer.ToString());
        Log?.Error($"No reply to '{command}' within {ResponseTimeout.TotalMilliseconds:0} ms");
        throw new TimeoutException("The radio didn't answer. Check that SmartSDR CAT is running and that this is a CAT port.");
    }

    private string ReadFor(TimeSpan duration)
    {
        var sb = new StringBuilder();
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            if (_port.BytesToRead > 0) sb.Append(_port.ReadExisting());
            else Thread.Sleep(5);
        }
        return sb.ToString();
    }

    private void DrainStale()
    {
        if (_port.BytesToRead > 0) Log?.Rx(_port.ReadExisting() + "   [unsolicited, discarded]");
        _port.DiscardInBuffer();
    }

    private void EnsureOpen()
    {
        if (!_port.IsOpen) throw new InvalidOperationException("Radio port is not open.");
    }
}
