using System.IO.Ports;
using SatTrack.Core.Rotator;

namespace SatTrack.Core.Radio;

/// <summary>The radio refused a command.</summary>
public sealed class CivException : Exception
{
    public CivException(string message) : base(message) { }
}

/// <summary>
/// ICOM CI-V radio control (IC-7700 and most other ICOM rigs).
/// Frames: FE FE [radio addr] [controller addr] [cmd] [data] FD. Frequencies are 5 BCD bytes,
/// least significant first. CI-V is a shared bus, so the radio's interface usually echoes our
/// own frames back; those are recognised by their "from" address and skipped.
/// </summary>
public sealed class IcomCivRadio : IRadio
{
    private readonly SerialPort _port;
    private readonly object _lock = new();
    private readonly List<byte> _rx = new();

    /// <summary>The radio's CI-V address. IC-7700 default is 74h.</summary>
    public byte RadioAddress { get; set; } = 0x74;

    /// <summary>This app's address on the bus. E0h is the conventional controller address.</summary>
    public byte ControllerAddress { get; set; } = 0xE0;

    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    public CommLog? Log { get; set; }

    public string PortName => _port.PortName;

    public string Description => $"ICOM CI-V ({RadioAddress:X2}h) on {_port.PortName}";

    public bool IsOpen => _port.IsOpen;

    public IcomCivRadio(string portName, int baudRate = 19200)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 200,
            WriteTimeout = 1000,
            // Keep DTR/RTS low: many USB interfaces for ICOM radios use them for CW keying or PTT.
            DtrEnable = false,
            RtsEnable = false,
        };
    }

    public void Open()
    {
        lock (_lock)
        {
            if (_port.IsOpen) return;
            Log?.Info($"Opening {_port.PortName} at {_port.BaudRate} baud, 8N1, DTR/RTS off; radio address {RadioAddress:X2}h");
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
        var reply = Transact(0x03, Array.Empty<byte>(), expectData: true, routine: true);
        if (reply.Data.Length < 5) throw new CivException("Frequency reply was too short.");
        return CivFrame.DecodeFrequency(reply.Data.AsSpan(0, 5));
    }

    public void SetFrequencyHz(long hz) =>
        Transact(0x05, CivFrame.EncodeFrequency(hz), expectData: false, routine: false);

    /// <summary>Sends one command and waits for the radio's answer (data reply or OK/NG).</summary>
    private CivFrame Transact(byte command, byte[] data, bool expectData, bool routine)
    {
        lock (_lock)
        {
            if (!_port.IsOpen) throw new InvalidOperationException("Radio port is not open.");
            DrainStale();

            var frame = new CivFrame(RadioAddress, ControllerAddress, command, data);
            var bytes = frame.ToBytes();
            Log?.Tx($"{frame.ToHex()}   [{frame.Describe()}]", routine);
            _port.Write(bytes, 0, bytes.Length);

            var deadline = DateTime.UtcNow + ResponseTimeout;
            var buf = new byte[256];
            while (DateTime.UtcNow < deadline)
            {
                int n = _port.BytesToRead;
                if (n == 0) { Thread.Sleep(5); continue; }

                n = _port.Read(buf, 0, Math.Min(n, buf.Length));
                for (int i = 0; i < n; i++) _rx.Add(buf[i]);

                foreach (var f in CivFrame.Extract(_rx))
                {
                    bool echo = f.From == ControllerAddress;
                    Log?.Rx($"{f.ToHex()}   [{(echo ? "echo of our command" : f.Describe())}]", routine);
                    if (echo) continue;
                    if (f.From != RadioAddress || f.To != ControllerAddress) continue; // other traffic / broadcasts

                    if (f.Command == CivFrame.Ng)
                    {
                        Log?.Error($"Radio rejected command {command:X2}");
                        throw new CivException($"The radio rejected the command (NG). " +
                            (command == 0x05 ? "The frequency may be outside its range." : ""));
                    }
                    if (!expectData && f.Command == CivFrame.Ok) return f;
                    if (expectData && f.Command == command) return f;
                }
            }

            Log?.Error($"No reply to command {command:X2} within {ResponseTimeout.TotalMilliseconds:0} ms");
            throw new TimeoutException("The radio didn't answer. Check the CI-V address, baud rate and cable.");
        }
    }

    private void DrainStale()
    {
        if (_port.BytesToRead > 0)
        {
            var stale = new byte[_port.BytesToRead];
            int n = _port.Read(stale, 0, stale.Length);
            Log?.Rx($"{CivFrame.Hex(stale.Take(n))}   [unsolicited, discarded]");
        }
        _rx.Clear();
    }
}
