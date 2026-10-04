using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;

namespace SatTrack.Core.Rotator;

public sealed class Gs232bException : Exception
{
    public Gs232bException(string message) : base(message) { }
}

/// <summary>
/// Yaesu GS-232B computer control interface.
/// Commands are ASCII terminated by CR. Defaults: 9600 baud, 8N1, no flow control.
/// The parser tolerates echoed commands, CR or CRLF endings and variable spacing,
/// which differ between firmware revisions.
/// </summary>
public sealed class Gs232bRotator : IRotator
{
    private readonly SerialPort _port;
    private readonly object _lock = new();

    // Requiring a line terminator after the last number avoids parsing a partial reply.
    private static readonly Regex PositionRegex =
        new(@"AZ=\s*(\d{1,3})\s+EL=\s*(\d{1,3})\s*[\r\n]", RegexOptions.Compiled);

    /// <summary>Upper azimuth limit. 450 for P45 mode, 360 for P36 mode.</summary>
    public int MaxAzimuth { get; set; } = 450;

    /// <summary>Upper elevation limit. 180 for rotators that support flip mode, otherwise 90.</summary>
    public int MaxElevation { get; set; } = 180;

    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long to listen for an echo or a "?>" error after a command with no reply.</summary>
    public TimeSpan CommandSettleTime { get; set; } = TimeSpan.FromMilliseconds(80);

    public string PortName => _port.PortName;

    /// <summary>Optional log of all traffic (shown in the serial debug window).</summary>
    public CommLog? Log { get; set; }

    public string Description => $"GS-232B on {_port.PortName}";

    public bool IsOpen => _port.IsOpen;

    public Gs232bRotator(string portName, int baudRate = 9600)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            Encoding = Encoding.ASCII,
            ReadTimeout = 200,
            WriteTimeout = 1000,
            DtrEnable = true,
            RtsEnable = true,
        };
    }

    public void Open()
    {
        lock (_lock)
        {
            if (_port.IsOpen) return;
            Log?.Info($"Opening {_port.PortName} at {_port.BaudRate} baud, 8N1, no flow control");
            try
            {
                _port.Open();
            }
            catch (Exception ex)
            {
                Log?.Error($"Couldn't open {_port.PortName}: {ex.Message}");
                throw;
            }
            Thread.Sleep(100);
            if (_port.BytesToRead > 0) Log?.Rx(_port.ReadExisting());
            _port.DiscardInBuffer();
            Log?.Info($"{_port.PortName} open");
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
        try { if (_port.IsOpen) Stop(); } catch { /* best effort */ }
        Close();
        _port.Dispose();
    }

    // ---------- IRotator ----------

    public RotatorPosition GetPosition()
    {
        Match m = Query("C2", PositionRegex);
        return new RotatorPosition(
            int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    public void GoTo(double azimuth, double elevation)
    {
        int az = ClampRound(azimuth, 0, MaxAzimuth);
        int el = ClampRound(elevation, 0, MaxElevation);
        SendCommand(string.Format(CultureInfo.InvariantCulture, "W{0:000} {1:000}", az, el));
    }

    public void Stop() => SendCommand("S");

    // ---------- Extra GS-232B commands ----------

    public void GoToAzimuth(double azimuth) =>
        SendCommand(string.Format(CultureInfo.InvariantCulture, "M{0:000}", ClampRound(azimuth, 0, MaxAzimuth)));

    public void StopAzimuth() => SendCommand("A");
    public void StopElevation() => SendCommand("E");
    public void RotateClockwise() => SendCommand("R");
    public void RotateCounterClockwise() => SendCommand("L");
    public void RotateUp() => SendCommand("U");
    public void RotateDown() => SendCommand("D");

    /// <summary>Azimuth rotation speed, 1 (slowest) to 4 (fastest).</summary>
    public void SetAzimuthSpeed(int level)
    {
        if (level is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(level), "Speed must be 1-4.");
        SendCommand($"X{level}");
    }

    /// <summary>Switch between 450° (true) and 360° (false) azimuth modes.</summary>
    public void Set450DegreeMode(bool enabled)
    {
        SendCommand(enabled ? "P45" : "P36");
        MaxAzimuth = enabled ? 450 : 360;
    }

    // ---------- Low level ----------

    /// <summary>Send a command that produces no data reply. Throws if the controller answers "?>".</summary>
    public void SendCommand(string command)
    {
        lock (_lock)
        {
            EnsureOpen();
            DiscardStale();
            Log?.Tx(command + "\r");
            _port.Write(command + "\r");

            string reply = ReadFor(CommandSettleTime);
            if (reply.Length > 0) Log?.Rx(reply);
            if (reply.Contains("?>"))
            {
                Log?.Error($"Controller rejected '{command}'");
                throw new Gs232bException($"Controller rejected command '{command}'.");
            }
        }
    }

    /// <summary>Send a raw command and return whatever arrives within the given time. Useful for testing.</summary>
    public string SendRaw(string command, TimeSpan listenFor)
    {
        lock (_lock)
        {
            EnsureOpen();
            DiscardStale();
            Log?.Tx(command + "\r");
            _port.Write(command + "\r");
            string reply = ReadFor(listenFor);
            Log?.Rx(reply);
            return reply;
        }
    }

    private Match Query(string command, Regex expected)
    {
        lock (_lock)
        {
            EnsureOpen();
            DiscardStale();
            Log?.Tx(command + "\r");
            _port.Write(command + "\r");

            var buffer = new StringBuilder();
            DateTime deadline = DateTime.UtcNow + ResponseTimeout;

            while (DateTime.UtcNow < deadline)
            {
                if (_port.BytesToRead > 0)
                {
                    buffer.Append(_port.ReadExisting());
                    string text = buffer.ToString();

                    if (text.Contains("?>"))
                    {
                        Log?.Rx(text);
                        Log?.Error($"Controller rejected '{command}'");
                        throw new Gs232bException($"Controller rejected command '{command}'.");
                    }

                    Match m = expected.Match(text);
                    if (m.Success)
                    {
                        // Pick up the rest of the line ending if it's already arrived.
                        if (_port.BytesToRead > 0) text += _port.ReadExisting();
                        Log?.Rx(text);
                        return m;
                    }
                }
                else
                {
                    Thread.Sleep(10);
                }
            }

            if (buffer.Length > 0) Log?.Rx(buffer.ToString());
            Log?.Error($"No valid reply to '{command}' within {ResponseTimeout.TotalMilliseconds:0} ms");
            throw new TimeoutException(
                $"No valid reply to '{command}'. Received: '{Escape(buffer.ToString())}'");
        }
    }

    private string ReadFor(TimeSpan duration)
    {
        var buffer = new StringBuilder();
        DateTime deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            if (_port.BytesToRead > 0) buffer.Append(_port.ReadExisting());
            else Thread.Sleep(10);
        }
        return buffer.ToString();
    }

    /// <summary>Logs and drops anything unexpected waiting in the input buffer.</summary>
    private void DiscardStale()
    {
        if (_port.BytesToRead > 0)
        {
            string stale = _port.ReadExisting();
            Log?.Rx(stale + "   (unexpected, discarded)");
        }
        _port.DiscardInBuffer();
    }

    private void EnsureOpen()
    {
        if (!_port.IsOpen) throw new InvalidOperationException("Serial port is not open.");
    }

    private static int ClampRound(double value, int min, int max) =>
        Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), min, max);

    private static string Escape(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");
}
