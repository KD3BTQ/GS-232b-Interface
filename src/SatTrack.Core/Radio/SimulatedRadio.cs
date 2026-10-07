using SatTrack.Core.Rotator;

namespace SatTrack.Core.Radio;

/// <summary>Stand-in radio for simulation mode. Logs the commands a real radio would see (Flex CAT or ICOM CI-V).</summary>
public sealed class SimulatedRadio : IRadio
{
    private readonly object _lock = new();
    private long _hz;
    private bool _open;

    public byte RadioAddress { get; set; } = 0x74;
    public byte ControllerAddress { get; set; } = 0xE0;
    public CommLog? Log { get; set; }
    public RadioType Protocol { get; set; } = RadioType.FlexCat;

    /// <summary>Frequencies outside this range are rejected, like a real radio would.</summary>
    public long MinHz { get; set; } = 0;
    public long MaxHz { get; set; } = 99_999_999_999;

    public string Description => "Simulated radio";
    public bool IsOpen { get { lock (_lock) return _open; } }

    public SimulatedRadio(long startHz = 29_400_000) => _hz = startHz;

    public void Open()
    {
        lock (_lock)
        {
            _open = true;
            Log?.Info($"Simulated radio connected (no serial port; {(Protocol == RadioType.FlexCat ? "CAT commands" : "CI-V frames")} below are what the radio would see)");
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            if (_open) Log?.Info("Simulated radio disconnected");
            _open = false;
        }
    }

    public void Dispose() => Close();

    public long GetFrequencyHz()
    {
        lock (_lock)
        {
            Ensure();
            if (Protocol == RadioType.FlexCat)
            {
                Log?.Tx("FA;", routine: true);
                Log?.Rx($"FA{_hz:00000000000};", routine: true);
                return _hz;
            }
            var q = new CivFrame(RadioAddress, ControllerAddress, 0x03, Array.Empty<byte>());
            var r = new CivFrame(ControllerAddress, RadioAddress, 0x03, CivFrame.EncodeFrequency(_hz));
            Log?.Tx($"{q.ToHex()}   [{q.Describe()}]", routine: true);
            Log?.Rx($"{r.ToHex()}   [{r.Describe()}]", routine: true);
            return _hz;
        }
    }

    public void SetFrequencyHz(long hz)
    {
        lock (_lock)
        {
            Ensure();
            bool ok = hz >= MinHz && hz <= MaxHz;
            if (Protocol == RadioType.FlexCat)
            {
                Log?.Tx($"FA{hz:00000000000};   [set frequency {hz / 1e6:0.000000} MHz]");
                if (!ok)
                {
                    Log?.Rx("?;");
                    throw new CivException("The radio rejected the command (?;). The frequency may be outside its range.");
                }
                _hz = hz;
                return;
            }
            var q = new CivFrame(RadioAddress, ControllerAddress, 0x05, CivFrame.EncodeFrequency(hz));
            Log?.Tx($"{q.ToHex()}   [{q.Describe()}]");
            var r = new CivFrame(ControllerAddress, RadioAddress, ok ? CivFrame.Ok : CivFrame.Ng, Array.Empty<byte>());
            Log?.Rx($"{r.ToHex()}   [{r.Describe()}]");
            if (!ok) throw new CivException("The radio rejected the command (NG). The frequency may be outside its range.");
            _hz = hz;
        }
    }

    private void Ensure()
    {
        if (!_open) throw new InvalidOperationException("Simulated radio is not connected.");
    }
}
