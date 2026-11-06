using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace CNCStudio.Core.Grbl;

/// <summary>What the demo machine panel shows: the "real" carriage position next to what GRBL reports.</summary>
public sealed record SimSnapshot(string State, bool Homed, double[] MPos, double[] Physical, double[] PhysicalTravel,
    string Pins, double Feed, double Spindle, int QueuedBlocks, bool CriticalAlarm);

/// <summary>
/// A simulated GRBL 1.1 controller on a demo machine with X-Carve 750 properties (750 × 750 × 65 mm,
/// limit switches at the positive end of each axis, Z switch at the top). It speaks the same text
/// protocol as the real board, so everything in CNC Studio can be tried without a machine.
/// <para>
/// Behaviour follows docs/GRBL-verifierat.md: alarm lock after start with $22=1, homing Z first and
/// then X+Y with MPos = −$27 afterwards, soft limits −$13x…0, hard limits on every switch change,
/// $J= rules (Idle/Jog, F required, error:15), G10 L20, G53, $$/$#/$G, settings only in Idle/Alarm.
/// </para>
/// <para>
/// Simplifications (documented in docs/Demomaskin.md): no acceleration, no arcs (G2/G3), no probing,
/// no G28/G30; homing has no slow locate phase. The simulator is never proof that something works
/// on the real machine.
/// </para>
/// </summary>
public sealed class SimulatedGrbl : IGrblTransport
{
    public const string Version = "Grbl 1.1h ['$' for help]";
    private const int PlannerSize = 15;
    private static readonly string[] AxisNames = { "X", "Y", "Z" };

    /// <summary>Typical X-Carve 750 values for a demo. Not Inventables' official factory settings.</summary>
    public static IReadOnlyDictionary<int, double> DefaultSettings { get; } = new SortedDictionary<int, double>
    {
        [0] = 10, [1] = 255, [2] = 0, [3] = 0, [4] = 0, [5] = 0, [6] = 0, [10] = 1, [11] = 0.02, [12] = 0.01,
        [13] = 0, [20] = 1, [21] = 1, [22] = 1, [23] = 3, [24] = 25, [25] = 750, [26] = 250, [27] = 1,
        [30] = 30000, [31] = 0, [32] = 0, [100] = 40, [101] = 40, [102] = 200, [110] = 8000, [111] = 8000,
        [112] = 500, [120] = 500, [121] = 500, [122] = 50, [130] = 750, [131] = 750, [132] = 65,
    };

    private readonly object _sync = new();
    private readonly object _emitLock = new();
    private readonly ConcurrentQueue<string> _out = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Timer? _timer;
    private double _lastTick;

    // "EEPROM": kept across resets and reconnects.
    private readonly SortedDictionary<int, double> _settings = new(DefaultSettings.ToDictionary(kv => kv.Key, kv => kv.Value));
    private readonly double[][] _wcs = Enumerable.Range(0, 6).Select(_ => new double[3]).ToArray();

    // Physical machine: carriage position relative to the switches (0 = on the switch, −travel = far end).
    private readonly double[] _physTravel = { 750, 750, 65 };
    private readonly double[] _phys = { -375, -375, -30 };
    /// <summary>Where the switches physically sit: X and Y at the front-left (minus) end, Z at the top.</summary>
    private readonly bool[] _switchAtMin = { true, true, false };
    private readonly double _speed;

    /// <param name="speedFactor">Runs time faster (tests); 1 = real time.</param>
    public SimulatedGrbl(double speedFactor = 1) { _speed = Math.Max(0.1, speedFactor); }
    private readonly bool[] _pressedByHand = new bool[3];
    private bool[] _lastPins = new bool[3];

    // Controller state.
    private readonly double[] _pos = new double[3];        // MPos (GRBL's step counter)
    private double[] _planned = new double[3];             // parser position = end of the queue
    private readonly double[] _g92 = new double[3];
    private int _activeWcs;
    private bool _absolute = true, _inches, _rapidMode = true, _feedSet, _checkMode;
    private double _feed, _spindle, _currentFeed;
    private bool _alarm, _critical, _homed, _hold, _holdDecel, _homing;
    private readonly LinkedList<Block> _queue = new();
    private string? _deferredLine;                         // waits for room in the planner
    private int _reportCount;
    private string _lastWco = "";

    private enum BlockKind { Move, Dwell, Pause, End, HomeSeek, HomePull, HomeDone }

    private sealed class Block
    {
        public BlockKind Kind;
        public double[] Target = new double[3];
        public double? FeedMmMin;                          // null = rapid
        public bool Jog;
        public double Seconds;                             // dwell
        public bool OkWhenDone;                            // GRBL syncs the buffer before answering
        public bool[] Axes = new bool[3];                  // homing axes
        public double Travelled;
    }

    public event Action<string>? DataReceived;
    public bool IsOpen { get; private set; }
    public IReadOnlyDictionary<int, double> Settings { get { lock (_sync) return new Dictionary<int, double>(_settings); } }

    // ---- Transport ----

    /// <summary>Opening the port resets the board (like the Arduino on an X-Carve): position unknown.</summary>
    public void Open()
    {
        lock (_sync)
        {
            IsOpen = true;
            _homed = false;
            PowerUpCounters();
            ResetController(fromMotion: false);
        }
        _lastTick = _clock.Elapsed.TotalSeconds;
        _timer ??= new Timer(_ => Tick(), null, 20, 20);
        Flush();
    }

    public void Close() { lock (_sync) IsOpen = false; _timer?.Dispose(); _timer = null; }
    /// <summary>The client disposes its transport on disconnect; the demo machine keeps its state.</summary>
    public void Dispose() => Close();

    public void WriteByte(byte value)
    {
        lock (_sync)
        {
            if (!IsOpen) return;
            switch (value)
            {
                case GrblProtocol.StatusReport: Emit(StatusReport()); break;
                case GrblProtocol.SoftReset: SoftReset(); break;
                case GrblProtocol.FeedHold: FeedHold(); break;
                case GrblProtocol.CycleStartResume: Resume(); break;
                case GrblProtocol.JogCancel: JogCancel(); break;
                case GrblProtocol.FeedOverrideReset: SetFeedOv(100); break;
                case GrblProtocol.FeedOverrideCoarsePlus: SetFeedOv(_feedOv + 10); break;
                case GrblProtocol.FeedOverrideCoarseMinus: SetFeedOv(_feedOv - 10); break;
                case GrblProtocol.FeedOverrideFinePlus: SetFeedOv(_feedOv + 1); break;
                case GrblProtocol.FeedOverrideFineMinus: SetFeedOv(_feedOv - 1); break;
                case GrblProtocol.RapidOverrideReset: _rapidOv = 100; _ovChanged = true; break;
                case GrblProtocol.RapidOverrideMedium: _rapidOv = 50; _ovChanged = true; break;
                case GrblProtocol.RapidOverrideLow: _rapidOv = 25; _ovChanged = true; break;
            }
        }
        Flush();
    }

    public void Write(string text)
    {
        lock (_sync)
        {
            if (!IsOpen) return;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim('\r', ' ');
                if (line.Length == 0) continue;
                if (_critical) continue;                   // GRBL waits for a reset and answers nothing
                if (_deferredLine is not null || QueueFull()) { _deferredLine = line; continue; }
                Execute(line);
            }
        }
        Flush();
    }

    // ---- Demo panel controls ----

    public SimSnapshot Snapshot()
    {
        lock (_sync)
            return new SimSnapshot(StateText(), _homed, (double[])_pos.Clone(), (double[])_phys.Clone(),
                (double[])_physTravel.Clone(), PinText(), _currentFeed, _spindle, _queue.Count, _critical);
    }

    /// <summary>Presses or releases a limit switch by hand (for the switch test).</summary>
    public void PressSwitch(int axis, bool pressed)
    {
        lock (_sync) { _pressedByHand[axis] = pressed; CheckPins(); }
        Flush();
    }

    /// <summary>Power off and on: the carriage stays where it is, GRBL forgets its position.</summary>
    public void PowerCycle()
    {
        lock (_sync)
        {
            _queue.Clear(); _deferredLine = null;
            _homed = false;
            PowerUpCounters();
            if (IsOpen) ResetController(fromMotion: false);
        }
        Flush();
    }

    /// <summary>Restores the demo settings ($$) and clears G54–G59.</summary>
    public void RestoreDefaults()
    {
        lock (_sync)
        {
            _settings.Clear();
            foreach (var (k, v) in DefaultSettings) _settings[k] = v;
            foreach (var w in _wcs) Array.Clear(w);
        }
    }

    // ---- Line execution ----

    private void Execute(string line)
    {
        if (line.StartsWith('$')) { System(line); return; }
        var clean = StripComments(line);
        if (clean.Length == 0) { Ok(); return; }
        if (_alarm) { Error(9); return; }                  // G-code lock in alarm
        if (StateText() == "Jog") { Error(9); return; }
        GCode(clean);
    }

    private void System(string line)
    {
        var u = line.ToUpperInvariant();
        if (u == "$$")
        {
            foreach (var (n, v) in _settings)
                Emit($"${n}={FormatSetting(n, v)}\r\n");
            Ok();
        }
        else if (u == "$#")
        {
            for (int i = 0; i < 6; i++) Emit($"[G{54 + i}:{V(_wcs[i])}]\r\n");
            Emit("[G28:0.000,0.000,0.000]\r\n[G30:0.000,0.000,0.000]\r\n");
            Emit($"[G92:{V(_g92)}]\r\n[TLO:0.000]\r\n[PRB:0.000,0.000,0.000:0]\r\n");
            Ok();
        }
        else if (u == "$G")
        {
            Emit($"[GC:{(_rapidMode ? "G0" : "G1")} G{54 + _activeWcs} G17 {(_inches ? "G20" : "G21")} {(_absolute ? "G90" : "G91")} G94 " +
                 $"{(_spindle > 0 ? "M3" : "M5")} M9 T0 F{N(_feed, 0)} S{N(_spindle, 0)}]\r\n");
            Ok();
        }
        else if (u == "$I") { Emit("[VER:1.1h.20190825:CNC Studio demomaskin]\r\n[OPT:V,15,128]\r\n"); Ok(); }
        else if (u == "$N") { Emit("$N0=\r\n$N1=\r\n"); Ok(); }
        else if (u.StartsWith("$N")) Ok();
        else if (u == "$X")
        {
            if (_alarm) { _alarm = false; Emit("[MSG:Caution: Unlocked]\r\n"); }
            Ok();
        }
        else if (u == "$H") Home();
        else if (u == "$C")
        {
            if (StateText() is not ("Idle" or "Check")) { Error(8); return; }
            _checkMode = !_checkMode;
            Emit(_checkMode ? "[MSG:Enabled]\r\n" : "[MSG:Disabled]\r\n");
            if (!_checkMode) { Ok(); SoftReset(); return; } // GRBL resets when leaving check mode
            Ok();
        }
        else if (u.StartsWith("$J=")) Jog(u[3..]);
        else if (u == "$RST=$") { foreach (var (k, v) in DefaultSettings) _settings[k] = v; Ok(); }
        else if (u == "$RST=#") { foreach (var w in _wcs) Array.Clear(w); Ok(); }
        else if (GrblProtocol.TryParseSetting(u, out var n, out var value)) WriteSetting(n, value);
        else Error(3);
    }

    private void WriteSetting(int n, double value)
    {
        if (StateText() is not ("Idle" or "Alarm")) { Error(8); return; }
        if (value < 0) { Error(4); return; }
        if (!_settings.ContainsKey(n)) { Error(3); return; }
        if (n == 20 && value == 1 && _settings[22] != 1) { Error(10); return; } // soft limits need homing
        _settings[n] = value;
        Ok();
    }

    private void Home()
    {
        if (_settings[22] != 1) { Error(5); return; }
        if (StateText() is not ("Idle" or "Alarm")) { Error(8); return; }
        _alarm = false; _homing = true; _homed = false;
        _queue.Clear();
        // Cycle 0: Z, then cycle 1: X and Y together (config.h HOMING_CYCLE_0/1).
        _queue.AddLast(new Block { Kind = BlockKind.HomeSeek, Axes = new[] { false, false, true } });
        _queue.AddLast(new Block { Kind = BlockKind.HomePull, Axes = new[] { false, false, true } });
        _queue.AddLast(new Block { Kind = BlockKind.HomeSeek, Axes = new[] { true, true, false } });
        _queue.AddLast(new Block { Kind = BlockKind.HomePull, Axes = new[] { true, true, false } });
        _queue.AddLast(new Block { Kind = BlockKind.HomeDone, OkWhenDone = true });
    }

    private void Jog(string body)
    {
        if (_alarm || StateText() is not ("Idle" or "Jog")) { Error(8); return; }
        var words = Words(StripComments(body), out int err);
        if (err != 0) { Error(err); return; }
        bool absolute = false, machine = false, inches = false;
        double? f = null; var target = new double?[3];
        foreach (var (letter, value) in words)
        {
            switch (letter)
            {
                case 'G' when value == 90: absolute = true; break;
                case 'G' when value == 91: absolute = false; break;
                case 'G' when value == 20: inches = true; break;
                case 'G' when value == 21: inches = false; break;
                case 'G' when value == 53: machine = true; break;
                case 'X': target[0] = value; break;
                case 'Y': target[1] = value; break;
                case 'Z': target[2] = value; break;
                case 'F': f = value; break;
                case 'N': break;
                default: Error(20); return;                 // G0/G1, S, T … are not allowed in a jog
            }
        }
        if (f is null) { Error(22); return; }
        if (target.All(t => t is null)) { Error(26); return; }
        double k = inches ? 25.4 : 1;
        var start = JogStart();
        var end = (double[])start.Clone();
        for (int i = 0; i < 3; i++)
        {
            if (target[i] is not double t) continue;
            end[i] = machine ? t * k : absolute ? t * k + Wco(i) : start[i] + t * k;
        }
        if (_settings[20] == 1 && !WithinSoftLimits(end)) { Error(15); return; } // jog.c: no alarm
        _queue.AddLast(new Block { Kind = BlockKind.Move, Target = end, FeedMmMin = f * k, Jog = true });
        _planned = (double[])end.Clone();
        Ok();
    }

    private double[] JogStart() => _queue.Count > 0 ? (double[])_planned.Clone() : (double[])_pos.Clone();

    private void GCode(string clean)
    {
        var words = Words(clean, out int err);
        if (err != 0) { Error(err); return; }
        bool g53 = false, g10 = false, g92 = false, dwell = false;
        int? motion = null, l = null, p = null, mCode = null;
        double? pValue = null;
        var axis = new double?[3];
        bool absolute = _absolute, inches = _inches;
        double? feed = null, spindle = null;
        foreach (var (letter, value) in words)
        {
            switch (letter)
            {
                case 'G':
                    switch (value)
                    {
                        case 0: motion = 0; break;
                        case 1: motion = 1; break;
                        case 4: dwell = true; break;
                        case 10: g10 = true; break;
                        case 17: case 94: break;
                        case 20: inches = true; break;
                        case 21: inches = false; break;
                        case 53: g53 = true; break;
                        case >= 54 and <= 59 when value == Math.Floor(value): _activeWcs = (int)value - 54; break;
                        case 90: absolute = true; break;
                        case 91: absolute = false; break;
                        case 92: g92 = true; break;
                        default: Error(20); return;
                    }
                    break;
                case 'M':
                    if (value is 0 or 2 or 30 or 3 or 4 or 5 or 8 or 9) mCode = (int)value;
                    else { Error(20); return; }
                    break;
                case 'X': axis[0] = value; break;
                case 'Y': axis[1] = value; break;
                case 'Z': axis[2] = value; break;
                case 'F': feed = value; break;
                case 'S': spindle = value; break;
                case 'P': pValue = value; p = (int)value; break;
                case 'L': l = (int)value; break;
                case 'N': case 'T': break;
                default: Error(20); return;
            }
        }
        double k = inches ? 25.4 : 1;
        _absolute = absolute; _inches = inches;
        if (feed is double fv) { if (fv < 0) { Error(4); return; } _feed = fv * k; _feedSet = true; }
        if (spindle is double sv) { if (sv < 0) { Error(4); return; } if (_spindle > 0 || mCode is 3 or 4) _spindle = sv; else _pendingS = sv; }

        if (g10)
        {
            if (l is not (2 or 20) || p is null or < 0 or > 6) { Error(20); return; }
            int index = p == 0 ? _activeWcs : p.Value - 1;
            for (int i = 0; i < 3; i++)
            {
                if (axis[i] is not double v) continue;     // axes not named keep their stored value
                _wcs[index][i] = l == 20 ? _planned[i] - _g92[i] - v * k : v * k;
            }
            Ok(); return;
        }
        if (g92)
        {
            for (int i = 0; i < 3; i++)
                if (axis[i] is double v) _g92[i] = _planned[i] - _wcs[_activeWcs][i] - v * k;
            Ok(); return;
        }
        if (dwell)
        {
            if (pValue is null) { Error(28); return; }
            if (pValue < 0) { Error(4); return; }
            Enqueue(new Block { Kind = BlockKind.Dwell, Seconds = pValue.Value, OkWhenDone = true });
            return;
        }
        if (mCode is 3 or 4) _spindle = spindle ?? _pendingS ?? _spindle;
        if (mCode == 5) _spindle = 0;

        if (motion is not null) _rapidMode = motion == 0;
        if (axis.Any(a => a is not null))
        {
            bool rapid = motion is null ? _rapidMode : motion == 0;
            if (!rapid && !_feedSet) { Error(22); return; }
            var end = (double[])_planned.Clone();
            for (int i = 0; i < 3; i++)
            {
                if (axis[i] is not double v) continue;
                end[i] = g53 ? v * k : absolute ? v * k + Wco(i) : _planned[i] + v * k;
            }
            if (_settings[20] == 1 && !_checkMode && !WithinSoftLimits(end))
            {
                RaiseAlarm(2, critical: false);
                return;
            }
            Enqueue(new Block { Kind = BlockKind.Move, Target = end, FeedMmMin = rapid ? null : _feed });
            _planned = end;
            if (mCode is null) return;
        }
        if (mCode == 0) { Enqueue(new Block { Kind = BlockKind.Pause, OkWhenDone = true }); return; }
        if (mCode is 2 or 30) { Enqueue(new Block { Kind = BlockKind.End, OkWhenDone = true }); return; }
        if (axis.All(a => a is null)) Ok();
    }

    private double? _pendingS;
    private int _feedOv = 100, _rapidOv = 100;
    private bool _ovChanged = true;

    /// <summary>config.h: feed override is clamped to 10–200 %.</summary>
    private void SetFeedOv(int value) { _feedOv = Math.Clamp(value, 10, 200); _ovChanged = true; }

    private void Enqueue(Block b)
    {
        if (_checkMode) { Ok(); return; }                  // $C: parsed and checked, never moved
        _queue.AddLast(b);
        if (!b.OkWhenDone) Ok();
    }

    // ---- Motion ----

    private void Tick()
    {
        double now = _clock.Elapsed.TotalSeconds, dt;
        lock (_sync)
        {
            dt = Math.Min(now - _lastTick, 0.1) * _speed;
            _lastTick = now;
            if (!IsOpen) return;
            Step(dt);
            if (_deferredLine is not null && !QueueFull() && !_critical)
            {
                var line = _deferredLine; _deferredLine = null;
                Execute(line);
            }
        }
        Flush();
    }

    private void Step(double dt)
    {
        _currentFeed = 0;
        if (_hold) { if (_holdDecel) _holdDecel = false; return; }
        while (dt > 1e-9 && _queue.First is { } node)
        {
            var b = node.Value;
            switch (b.Kind)
            {
                case BlockKind.Dwell:
                    b.Seconds -= dt; dt = 0;
                    if (b.Seconds <= 0) Complete(node);
                    break;
                case BlockKind.Pause:
                    _hold = true;                          // M0: program pause, resumed with ~
                    return;
                case BlockKind.End:
                    _spindle = 0; _rapidMode = false; _absolute = true; _activeWcs = 0; // M2/M30 resets
                    _feedOv = 100; _rapidOv = 100; _ovChanged = true;              // RESTORE_OVERRIDES_AFTER_PROGRAM_END
                    Emit("[MSG:Pgm End]\r\n");
                    Complete(node);
                    break;
                case BlockKind.Move:
                    dt = Move(b, dt);
                    if (_critical || _queue.First != node) return;
                    if (Same(_pos, b.Target)) Complete(node);
                    break;
                case BlockKind.HomeSeek:
                case BlockKind.HomePull:
                    dt = HomeMove(b, dt);
                    if (_critical || !_homing) return;
                    break;
                case BlockKind.HomeDone:
                    _homing = false; _homed = true;
                    // limits.c (HOMING_FORCE_SET_ORIGIN off): homed toward plus → MPos −$27;
                    // homed toward minus ($23 bit set) → MPos −$13x + $27. Machine space is always −$13x…0.
                    for (int i = 0; i < 3; i++)
                        _pos[i] = ((int)_settings[23] & (1 << i)) != 0 ? -_settings[130 + i] + _settings[27] : -_settings[27];
                    _planned = (double[])_pos.Clone();
                    Complete(node);
                    break;
            }
        }
    }

    private void Complete(LinkedListNode<Block> node)
    {
        _queue.Remove(node);
        if (node.Value.OkWhenDone) Ok();
    }

    /// <summary>Moves toward the block's target for up to dt seconds. Returns the time left over.</summary>
    private double Move(Block b, double dt)
    {
        var d = new double[3];
        double dist = 0;
        for (int i = 0; i < 3; i++) { d[i] = b.Target[i] - _pos[i]; dist += d[i] * d[i]; }
        dist = Math.Sqrt(dist);
        if (dist < 1e-9) return dt;
        // Speed: rapid = fastest the slowest moving axis allows; feed = F capped by each axis' max rate.
        double speed = double.MaxValue;
        for (int i = 0; i < 3; i++)
            if (Math.Abs(d[i]) > 1e-9) speed = Math.Min(speed, MaxRate(i) * dist / Math.Abs(d[i]));
        if (b.FeedMmMin is double f) speed = Math.Min(speed, b.Jog ? f : f * _feedOv / 100.0);
        else speed = speed * _rapidOv / 100.0;
        speed /= 60.0;
        _currentFeed = speed * 60;
        double step = speed * dt;
        double frac = Math.Min(1, step / dist);
        var delta = d.Select(x => x * frac).ToArray();
        Displace(delta);
        return step >= dist ? dt - dist / speed : 0;
    }

    private double HomeMove(Block b, double dt)
    {
        bool seek = b.Kind == BlockKind.HomeSeek;
        double rate = _settings[seek ? 25 : 24] / 60.0;
        double pull = _settings[27];
        bool done = true;
        var delta = new double[3];
        for (int i = 0; i < 3; i++)
        {
            if (!b.Axes[i]) continue;
            int dir = ((int)_settings[23] & (1 << i)) != 0 ? -1 : 1;
            if (seek)
            {
                if (SwitchTriggered(i)) continue;
                delta[i] = dir * rate * dt;
                done = false;
            }
            else
            {
                double remaining = pull - (b.Travelled);
                if (remaining > 1e-9) { delta[i] = -dir * Math.Min(rate * dt, remaining); done = false; }
            }
        }
        if (!seek) b.Travelled += delta.Where((_, i) => b.Axes[i]).Select(Math.Abs).DefaultIfEmpty(0).Max();
        if (seek) b.Travelled += rate * dt;
        DisplaceForHoming(delta);
        if (seek)                                          // the carriage stops where the switch trips
            for (int i = 0; i < 3; i++)
                if (b.Axes[i] && SwitchTriggered(i) && !_pressedByHand[i])
                    _phys[i] = _switchAtMin[i] ? -_physTravel[i] : 0;
        if (seek && !done && b.Travelled > 1.5 * b.Axes.Select((on, i) => on ? _settings[130 + i] : 0).Max())
        {
            RaiseAlarm(9, critical: true);                 // switch not found within the search distance
            return 0;
        }
        if (!seek && !done && b.Travelled > pull + 1e-9) done = true;
        if (!seek && done && b.Axes.Select((on, i) => on && SwitchTriggered(i)).Any(x => x))
        {
            RaiseAlarm(8, critical: true);                 // pull-off did not clear the switch
            return 0;
        }
        if (done) { _queue.RemoveFirst(); return dt; }
        return 0;
    }

    /// <summary>Moves counter and carriage together; the frame stops the carriage, a switch may trip.</summary>
    private void Displace(double[] delta)
    {
        for (int i = 0; i < 3; i++)
        {
            _pos[i] += delta[i];
            _phys[i] = Math.Clamp(_phys[i] + delta[i], -_physTravel[i] - 2, 2); // mechanical end stops
        }
        CheckPins();
    }

    private void DisplaceForHoming(double[] delta)
    {
        for (int i = 0; i < 3; i++) _phys[i] = Math.Clamp(_phys[i] + delta[i], -_physTravel[i] - 2, 2);
        _lastPins = Enumerable.Range(0, 3).Select(SwitchTriggered).ToArray(); // expected during homing
    }

    private bool SwitchTriggered(int i) => _pressedByHand[i] || (_switchAtMin[i] ? _phys[i] <= -_physTravel[i] : _phys[i] >= 0);

    private void CheckPins()
    {
        var pins = Enumerable.Range(0, 3).Select(SwitchTriggered).ToArray();
        bool changed = !pins.SequenceEqual(_lastPins);
        _lastPins = pins;
        // limits.c: with hard limits on, any change on a limit input stops the machine (also in Idle).
        if (changed && _settings[21] == 1 && !_homing && !_critical) RaiseAlarm(1, critical: true);
    }

    // ---- Real-time commands ----

    private void SoftReset()
    {
        bool moving = _queue.Count > 0 && !_hold || _homing;
        if (_homing) Emit("ALARM:6\r\n");                  // reset during homing
        else if (moving) Emit("ALARM:3\r\n");              // abort during cycle: position lost
        if (moving) { _homed = false; _alarm = true; }
        ResetController(fromMotion: moving);
    }

    private void ResetController(bool fromMotion)
    {
        _queue.Clear(); _deferredLine = null;
        _hold = _holdDecel = _homing = _critical = _checkMode = false;
        _absolute = true; _inches = false; _rapidMode = true; _activeWcs = 0; _spindle = 0; _feed = 0; _feedSet = false;
        Array.Clear(_g92);
        _planned = (double[])_pos.Clone();
        _lastPins = Enumerable.Range(0, 3).Select(SwitchTriggered).ToArray();
        // HOMING_INIT_LOCK: with homing enabled, GRBL starts locked until $H (or $X).
        if (_settings[22] == 1 && !_homed) _alarm = true;
        Emit("\r\n" + Version + "\r\n");
        if (_alarm) Emit("[MSG:'$H'|'$X' to unlock]\r\n");
    }

    private void FeedHold()
    {
        var state = StateText();
        if (state == "Jog") { JogCancel(); return; }       // a feed hold during a jog cancels it
        if (state == "Run") { _hold = true; _holdDecel = true; }
    }

    private void Resume()
    {
        if (!_hold) return;
        _hold = false; _holdDecel = false;
        if (_queue.First is { Value.Kind: BlockKind.Pause } node) Complete(node); // M0 answers ok on resume
    }

    private void JogCancel()
    {
        if (StateText() != "Jog") return;
        var jogs = _queue.Where(b => b.Jog).ToList();
        foreach (var j in jogs) _queue.Remove(j);
        _planned = (double[])_pos.Clone();
    }

    private void RaiseAlarm(int code, bool critical)
    {
        _queue.Clear(); _deferredLine = null; _homing = false; _hold = false;
        _alarm = true;
        if (critical) { _critical = true; _homed = false; }
        Emit($"ALARM:{code}\r\n");
        if (critical) Emit("[MSG:Reset to continue]\r\n");
        _planned = (double[])_pos.Clone();
    }

    // ---- Reporting ----

    private string StateText()
    {
        if (_alarm) return "Alarm";
        if (_homing) return "Home";
        if (_hold) return _holdDecel ? "Hold:1" : "Hold:0";
        if (_queue.First is { } n) return n.Value.Jog ? "Jog" : "Run";
        return _checkMode ? "Check" : "Idle";
    }

    private string StatusReport()
    {
        var sb = new StringBuilder("<").Append(StateText());
        var wco = Enumerable.Range(0, 3).Select(Wco).ToArray();
        if (((int)_settings[10] & 1) != 0) sb.Append("|MPos:").Append(V(_pos));
        else sb.Append("|WPos:").Append(V(_pos.Select((p, i) => p - wco[i]).ToArray()));
        sb.Append("|FS:").Append(N(_currentFeed, 0)).Append(',').Append(N(_spindle, 0));
        var pins = PinText();
        if (pins.Length > 0) sb.Append("|Pn:").Append(pins);
        var w = V(wco);
        // report.c sends WCO only now and then (and when it changed).
        if (w != _lastWco || _reportCount++ % 10 == 0) { sb.Append("|WCO:").Append(w); _lastWco = w; }
        else if (_ovChanged || _reportCount % 10 == 5) { sb.Append($"|Ov:{_feedOv},{_rapidOv},100"); _ovChanged = false; }
        return sb.Append(">\r\n").ToString();
    }

    private string PinText()
    {
        bool invert = _settings[5] == 1;                  // $5 is applied before reporting
        return string.Concat(Enumerable.Range(0, 3).Where(i => SwitchTriggered(i) ^ invert).Select(i => AxisNames[i]));
    }

    // ---- Helpers ----

    private void PowerUpCounters()
    {
        Array.Clear(_pos);                                 // GRBL counts from 0 wherever the carriage is
        _planned = new double[3];
    }

    private double Wco(int i) => _wcs[_activeWcs][i] + _g92[i];
    private double MaxRate(int i) => Math.Max(1, _settings[110 + i]);
    private bool QueueFull() => _queue.Count >= PlannerSize;

    private bool WithinSoftLimits(double[] p)
    {
        for (int i = 0; i < 3; i++)
            if (p[i] > 1e-6 || p[i] < -_settings[130 + i] - 1e-6) return false;
        return true;
    }

    private static bool Same(double[] a, double[] b) => a.Zip(b).All(t => Math.Abs(t.First - t.Second) < 1e-6);

    private static string StripComments(string line)
    {
        var sb = new StringBuilder();
        int depth = 0;
        foreach (var ch in line)
        {
            if (ch == ';' && depth == 0) break;
            if (ch == '(') { depth++; continue; }
            if (ch == ')') { if (depth > 0) depth--; continue; }
            if (depth == 0 && !char.IsWhiteSpace(ch)) sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }

    private static List<(char Letter, double Value)> Words(string s, out int error)
    {
        error = 0;
        var list = new List<(char, double)>();
        int i = 0;
        while (i < s.Length)
        {
            char letter = s[i];
            if (letter < 'A' || letter > 'Z') { error = 1; return list; }
            int start = ++i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] is '.' or '-' or '+')) i++;
            if (!double.TryParse(s[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { error = 2; return list; }
            list.Add((letter, v));
        }
        return list;
    }

    private static string FormatSetting(int n, double v) =>
        GrblSettings.Catalog.TryGetValue(n, out var info) && info.Kind != GrblSettingKind.Number
            ? ((long)v).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.000", CultureInfo.InvariantCulture);

    private static string V(double[] v) => string.Join(",", v.Select(x => x.ToString("0.000", CultureInfo.InvariantCulture)));
    private static string N(double v, int d) => Math.Round(v, d).ToString(CultureInfo.InvariantCulture);

    private void Ok() => Emit("ok\r\n");
    private void Error(int code) => Emit($"error:{code}\r\n");
    private void Emit(string text) => _out.Enqueue(text);

    /// <summary>Delivers queued output in order, outside the state lock.</summary>
    private void Flush()
    {
        lock (_emitLock)
            while (_out.TryDequeue(out var text)) DataReceived?.Invoke(text);
    }
}
