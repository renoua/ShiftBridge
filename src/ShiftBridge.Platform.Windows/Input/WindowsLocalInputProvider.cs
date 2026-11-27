using System.Runtime.InteropServices;
using System.Threading.Channels;
using ShiftBridge.Core.Input;
using ShiftBridge.Platform.Input;

namespace ShiftBridge.Platform.Windows.Input;

/// <summary>
/// Clavier (hook global), manettes Xbox (XInput) et joysticks HID (Raw Input) pour Windows.
/// Un thread dédié héberge la fenêtre de messages, le hook clavier et Raw Input. Tout ce qui est détecté passe par une file
/// unique : les rappels du hook et de la fenêtre ne font jamais le travail de <c>BridgeService</c> eux-mêmes (un hook trop lent
/// est retiré par Windows).
/// </summary>
public sealed class WindowsLocalInputProvider : ILocalInputProvider
{
    private const uint WmApply = Native.WM_APP + 1, WmQuit = Native.WM_APP + 2;
    private const string KeyboardSource = "kbd";

    private readonly InputAggregator _aggregator = new();
    private readonly Channel<List<ButtonEvent>> _queue = Channel.CreateUnbounded<List<ButtonEvent>>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _consumer;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Native.WndProc _wndProc; // champ : ne doit pas être collecté tant que la fenêtre existe
    private readonly HashSet<ButtonKey> _swallowedForCapture = []; // touches capturées : leur relâchement ne doit pas sortir
    private readonly object _devicesLock = new();

    private nint _hwnd;
    private KeyboardHook? _hook;
    private XInputPoller? _xinput;
    private RawHidJoysticks? _joysticks;
    private volatile bool _wantKeyboard, _wantGamepads, _disposed;
    private IReadOnlySet<ButtonKey> _bound = new HashSet<ButtonKey>();
    private bool _suppress = true;
    private volatile bool _captureNext;
    private LocalInputDevice[] _devices = [];

    public WindowsLocalInputProvider()
    {
        _wndProc = WndProc;
        _consumer = Task.Run(ConsumeAsync);
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "ShiftBridge.LocalInput" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(3));
    }

    public event Action? DevicesChanged;
    public event Action<IReadOnlyList<ButtonEvent>>? ButtonEvents;
    public event Action<string>? Log;
    public event Action<ButtonKey>? Captured;

    public IReadOnlyList<LocalInputDevice> Devices { get { lock (_devicesLock) return _devices; } }

    public bool CaptureNext { get => _captureNext; set => _captureNext = value; }

    public bool CanVibrate => _xinput?.ConnectedSlots.Count > 0;

    public Task VibrateAsync() => _xinput?.VibrateAsync() ?? Task.CompletedTask;

    // ---------- Configuration (appelable de n'importe quel thread) ----------

    public void Configure(bool keyboard, bool gamepads)
    {
        if (_disposed) return;
        _wantKeyboard = keyboard; _wantGamepads = gamepads;
        Post(WmApply);
    }

    public void SetBoundKeys(IReadOnlySet<ButtonKey> keys, bool suppress)
    {
        _bound = keys; _suppress = suppress;
        _hook?.SetBound(keys, suppress);
    }

    private void Post(uint msg)
    {
        if (_hwnd != 0) Native.PostMessageW(_hwnd, msg, 0, 0);
    }

    // ---------- Thread de messages ----------

    private void MessageLoop()
    {
        const string className = "ShiftBridgeLocalInputSink";
        nint instance = Native.GetModuleHandleW(null);
        try
        {
            var wc = new Native.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = instance, lpszClassName = className,
            };
            Native.RegisterClassExW(ref wc);
            _hwnd = Native.CreateWindowExW(0, className, "ShiftBridge input", 0, 0, 0, 0, 0, Native.HWND_MESSAGE, 0, instance, 0);
            if (_hwnd == 0) { Log?.Invoke("Entrées locales : fenêtre de messages impossible à créer."); return; }

            _hook = new KeyboardHook(OnKey, () => _captureNext);
            _hook.SetBound(_bound, _suppress);
            _xinput = new XInputPoller(_aggregator, Enqueue, RefreshDevices, m => Log?.Invoke(m));
            _joysticks = new RawHidJoysticks(_aggregator, Enqueue, RefreshDevices, m => Log?.Invoke(m));
        }
        finally { _ready.Set(); }
        if (_hwnd == 0) return;

        while (Native.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
        Native.UnregisterClassW(className, instance);
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case WmApply: Apply(); return 0;
                case WmQuit: Shutdown(); Native.DestroyWindow(hwnd); return 0;
                case Native.WM_INPUT: _joysticks?.OnInput(lParam); break; // puis DefWindowProc (obligatoire)
                case Native.WM_INPUT_DEVICE_CHANGE: _joysticks?.OnDeviceChange(wParam, lParam); return 0;
                case Native.WM_DESTROY: Native.PostQuitMessage(0); return 0;
            }
        }
        catch (Exception ex) { Log?.Invoke($"Entrées locales : {ex.Message}"); }
        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>Met le clavier, XInput et Raw Input en phase avec la configuration (thread des messages).</summary>
    private void Apply()
    {
        if (_disposed) return;

        if (_wantKeyboard && _hook is { IsInstalled: false } h)
        {
            if (h.Install()) Log?.Invoke("Clavier : écoute des touches assignées activée.");
            else Log?.Invoke("Clavier : hook impossible à installer.");
        }
        else if (!_wantKeyboard && _hook is { IsInstalled: true })
        {
            _hook.Uninstall();
            Log?.Invoke("Clavier : écoute désactivée.");
        }

        if (_wantGamepads)
        {
            _xinput?.Start();
            if (_joysticks is { } j && !j.IsRegistered) j.Register(_hwnd);
        }
        else
        {
            _joysticks?.Unregister();
            // XInputPoller n'a pas de pause : on le recrée à la prochaine activation.
            if (_xinput is not null)
            {
                // Arrêt hors de ce thread : il porte aussi le hook clavier, et Windows retire sans prévenir un hook qui ne
                // répond pas à temps (toutes les touches du système attendent pendant ce temps).
                var old = _xinput;
                _xinput = new XInputPoller(_aggregator, Enqueue, RefreshDevices, m => Log?.Invoke(m));
                _ = Task.Run(old.Dispose);
            }
        }
        RefreshDevices();
    }

    private void Shutdown()
    {
        _hook?.Dispose();
        _xinput?.Dispose();
        _joysticks?.Dispose();
    }

    // ---------- Événements ----------

    private void OnKey(ButtonKey key, bool down)
    {
        var events = _aggregator.Set(KeyboardSource, key, down);
        if (events.Count > 0) Enqueue(events);
    }

    private void Enqueue(List<ButtonEvent> events) => _queue.Writer.TryWrite(events);

    private async Task ConsumeAsync()
    {
        await foreach (var batch in _queue.Reader.ReadAllAsync())
        {
            try { Dispatch(batch); }
            catch (Exception ex) { Log?.Invoke($"Entrées locales : {ex.Message}"); }
        }
    }

    private void Dispatch(List<ButtonEvent> batch)
    {
        var output = new List<ButtonEvent>(batch.Count);
        foreach (var e in batch)
        {
            // Mode « appuie sur une touche » : le premier appui est signalé à part, sans déclencher d'action.
            if (e.Pressed && _captureNext)
            {
                _captureNext = false;
                _swallowedForCapture.Add(e.Button);
                Captured?.Invoke(e.Button);
                continue;
            }
            if (!e.Pressed && _swallowedForCapture.Remove(e.Button)) continue;
            output.Add(e);
        }
        if (output.Count > 0) ButtonEvents?.Invoke(output);
    }

    private void RefreshDevices()
    {
        var list = new List<LocalInputDevice>();
        if (_wantKeyboard) list.Add(new LocalInputDevice("kbd", "Clavier", LocalInputKind.Keyboard, false));
        if (_wantGamepads)
        {
            foreach (var slot in _xinput?.ConnectedSlots ?? [])
                list.Add(new LocalInputDevice($"xinput{slot}", $"Manette Xbox {slot + 1}", LocalInputKind.Gamepad, true));
            list.AddRange(_joysticks?.Devices ?? []);
        }
        lock (_devicesLock) _devices = [.. list];
        // Levé hors du thread du hook clavier (voir Apply) : l'interface peut y faire autant de travail qu'elle veut.
        ThreadPool.QueueUserWorkItem(_ => DevicesChanged?.Invoke());
    }

    // ---------- Arrêt ----------

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Post(WmQuit);
        await Task.Run(() => _thread.Join(TimeSpan.FromSeconds(2)));
        _queue.Writer.TryComplete();
        await _consumer;
        _ready.Dispose();
    }
}
