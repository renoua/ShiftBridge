using ShiftBridge.Core.Input;
using ShiftBridge.Platform.Input;

namespace ShiftBridge.Platform.Windows.Input;

/// <summary>
/// Lit les manettes XInput (Xbox 360/One/Series et compatibles) en tâche de fond, même quand une autre fenêtre a le focus
/// (contrairement à Windows.Gaming.Input). Les quatre emplacements sont lus ~125 fois par seconde ; un emplacement vide n'est
/// retesté que toutes les 2 s car l'interroger coûte cher.
/// </summary>
internal sealed class XInputPoller : IDisposable
{
    private const int Slots = 4;
    private const long ProbeEveryMs = 2000;
    private const int ActivePollMs = 8, IdlePollMs = 250;

    private readonly InputAggregator _aggregator;
    private readonly Action<List<ButtonEvent>> _enqueue;
    private readonly Action _devicesChanged;
    private readonly Action<string> _log;
    private readonly bool[] _connected = new bool[Slots];
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;

    public XInputPoller(InputAggregator aggregator, Action<List<ButtonEvent>> enqueue, Action devicesChanged, Action<string> log)
    {
        _aggregator = aggregator; _enqueue = enqueue; _devicesChanged = devicesChanged; _log = log;
    }

    /// <summary>Numéros des emplacements actuellement connectés.</summary>
    public IReadOnlyList<int> ConnectedSlots
    {
        get { lock (_connected) return Enumerable.Range(0, Slots).Where(i => _connected[i]).ToArray(); }
    }

    public void Start() => _task ??= Task.Run(() => PollAsync(_cts.Token));

    private async Task PollAsync(CancellationToken ct)
    {
        var mappers = Enumerable.Range(0, Slots).Select(_ => new XInputMapper()).ToArray();
        var lastPacket = new uint[Slots];
        var nextProbe = new long[Slots];
        // Résolution du minuteur à 1 ms (coûteuse en énergie pour tout le système) seulement quand une manette est branchée ;
        // sans manette, un tour toutes les 250 ms suffit à en détecter une nouvelle.
        bool highRes = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                long now = Environment.TickCount64;
                for (int i = 0; i < Slots; i++)
                {
                    bool wasConnected;
                    lock (_connected) wasConnected = _connected[i];
                    if (!wasConnected && now < nextProbe[i]) continue;

                    uint result;
                    Native.XINPUT_STATE st;
                    try { result = Native.XInputGetState((uint)i, out st); }
                    catch (DllNotFoundException) { _log("XInput indisponible : manettes Xbox ignorées."); return; }

                    if (result == 0)
                    {
                        if (!wasConnected)
                        {
                            lock (_connected) _connected[i] = true;
                            lastPacket[i] = uint.MaxValue; // force une première lecture
                            _devicesChanged();
                        }
                        if (st.PacketNumber == lastPacket[i]) continue; // rien n'a bougé
                        lastPacket[i] = st.PacketNumber;
                        var set = mappers[i].Map(new XInputSnapshot(st.Buttons, st.LeftTrigger, st.RightTrigger,
                            st.LeftX, st.LeftY, st.RightX, st.RightY));
                        var events = _aggregator.Replace($"xinput{i}", set);
                        if (events.Count > 0) _enqueue(events);
                    }
                    else
                    {
                        nextProbe[i] = now + ProbeEveryMs;
                        if (wasConnected)
                        {
                            lock (_connected) _connected[i] = false;
                            mappers[i].Reset();
                            var events = _aggregator.RemoveSource($"xinput{i}"); // relâche ce qui était tenu
                            if (events.Count > 0) _enqueue(events);
                            _devicesChanged();
                        }
                    }
                }
                bool any;
                lock (_connected) any = Array.IndexOf(_connected, true) >= 0;
                if (any != highRes)
                {
                    if (any) Native.timeBeginPeriod(1); else Native.timeEndPeriod(1);
                    highRes = any;
                }
                await Task.Delay(any ? ActivePollMs : IdlePollMs, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"Lecture XInput interrompue : {ex.Message}"); }
        finally
        {
            if (highRes) Native.timeEndPeriod(1);
            for (int i = 0; i < Slots; i++)
            {
                var events = _aggregator.RemoveSource($"xinput{i}");
                if (events.Count > 0) _enqueue(events);
                lock (_connected) _connected[i] = false;
            }
            _devicesChanged();
        }
    }

    /// <summary>Vibre brièvement toutes les manettes connectées.</summary>
    public async Task VibrateAsync(int ms = 120)
    {
        var slots = ConnectedSlots;
        if (slots.Count == 0) return;
        // Un appel qui en recouvre un autre prolonge la vibration : seul le dernier appel éteint les moteurs.
        int generation = Interlocked.Increment(ref _vibrationGeneration);
        try
        {
            var on = new Native.XINPUT_VIBRATION { Left = 40000, Right = 40000 };
            foreach (var i in slots) Native.XInputSetState((uint)i, ref on);
            await Task.Delay(ms);
        }
        catch (DllNotFoundException) { }
        finally
        {
            if (generation == Volatile.Read(ref _vibrationGeneration))
            {
                var off = new Native.XINPUT_VIBRATION();
                foreach (var i in slots) try { Native.XInputSetState((uint)i, ref off); } catch { /* ignoré */ }
            }
        }
    }

    private int _vibrationGeneration;

    public void Dispose()
    {
        _cts.Cancel();
        try { _task?.Wait(500); } catch { /* ignoré */ }
        _cts.Dispose();
    }
}
