using System.Runtime.InteropServices;
using ShiftBridge.Core.Input;
using ShiftBridge.Platform.Input;

namespace ShiftBridge.Platform.Windows.Input;

/// <summary>
/// Joysticks et manettes HID génériques (non XInput) via Raw Input, avec <c>RIDEV_INPUTSINK</c> pour continuer à les lire quand
/// une autre fenêtre a le focus. Les manettes XInput (chemin contenant « IG_ ») sont ignorées : <see cref="XInputPoller"/> s'en
/// charge, sinon chaque bouton arriverait en double. À utiliser depuis le thread de la fenêtre de messages uniquement.
/// </summary>
internal sealed class RawHidJoysticks : IDisposable
{
    private const ushort PageGenericDesktop = 0x01, PageButton = 0x09;
    private const ushort UsageJoystick = 0x04, UsageGamepad = 0x05, UsageHat = 0x39;
    private static readonly string[] AxisNames = ["X", "Y", "Z", "Rx", "Ry", "Rz"]; // usages 0x30..0x35

    private sealed class Axis
    {
        public ushort Usage; public int Min, Max, BitSize;
        public string Name => AxisNames[Usage - 0x30];
        public bool Ignored;      // glissière dont le repos n'est pas au centre : serait « appuyée » en permanence
        public bool Calibrated;
        public int Direction;     // -1, 0, +1
    }

    private sealed class Device
    {
        public nint Handle; public string Path = ""; public string Id = ""; public string Name = "";
        public nint Preparsed; public int ReportLength;
        public uint MaxButtons;
        public Axis[] Axes = [];
        public (int Min, int Max)? Hat;
        /// <summary>Manettes Nintendo en rapport complet 0x30 : décodage dédié (le descripteur HID ne le décrit pas).</summary>
        public SwitchProDecoder? Switch;
        public bool FirstReportLogged;
        public string Source => "joy" + Handle;

        // Dernier état lu de chaque partie : un rapport qui ne la contient pas (périphérique à plusieurs identifiants de
        // rapport) la laisse telle quelle au lieu de la relâcher.
        public List<ButtonKey> LastButtons = [];
        public List<ButtonKey> LastHat = [];

        // Tampons réutilisés (jusqu'à 1000 rapports par seconde) et identités de touches mises en cache.
        public byte[] Report = [];
        public ushort[] Usages = [];
        public readonly Dictionary<ushort, ButtonKey> ButtonKeys = [];
        public readonly Dictionary<string, ButtonKey> NamedKeys = [];

        public ButtonKey Button(ushort usage)
        {
            if (!ButtonKeys.TryGetValue(usage, out var k)) ButtonKeys[usage] = k = ButtonKey.Joystick(Id, "B" + usage);
            return k;
        }

        public ButtonKey Named(string name)
        {
            if (!NamedKeys.TryGetValue(name, out var k)) NamedKeys[name] = k = ButtonKey.Joystick(Id, name);
            return k;
        }
    }

    private readonly InputAggregator _aggregator;
    private readonly Action<List<ButtonEvent>> _enqueue;
    private readonly Action _devicesChanged;
    private readonly Action<string> _log;
    private readonly Dictionary<nint, Device?> _devices = []; // null = périphérique examiné et ignoré
    private nint _hwnd;

    public RawHidJoysticks(InputAggregator aggregator, Action<List<ButtonEvent>> enqueue, Action devicesChanged, Action<string> log)
    {
        _aggregator = aggregator; _enqueue = enqueue; _devicesChanged = devicesChanged; _log = log;
    }

    private volatile LocalInputDevice[] _snapshot = [];
    public IReadOnlyList<LocalInputDevice> Devices => _snapshot;

    public bool IsRegistered => _hwnd != 0;

    // ---------- Cycle de vie ----------

    public void Register(nint hwnd)
    {
        _hwnd = hwnd;
        uint flags = Native.RIDEV_INPUTSINK | Native.RIDEV_DEVNOTIFY;
        var regs = new[]
        {
            new Native.RAWINPUTDEVICE { UsagePage = PageGenericDesktop, Usage = UsageJoystick, Flags = flags, Target = hwnd },
            new Native.RAWINPUTDEVICE { UsagePage = PageGenericDesktop, Usage = UsageGamepad, Flags = flags, Target = hwnd },
        };
        if (!Native.RegisterRawInputDevices(regs, (uint)regs.Length, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
            _log($"Raw Input : enregistrement des joysticks impossible (erreur {Marshal.GetLastWin32Error()}).");
        Enumerate();
    }

    public void Unregister()
    {
        if (_hwnd == 0) return;
        var regs = new[]
        {
            new Native.RAWINPUTDEVICE { UsagePage = PageGenericDesktop, Usage = UsageJoystick, Flags = Native.RIDEV_REMOVE },
            new Native.RAWINPUTDEVICE { UsagePage = PageGenericDesktop, Usage = UsageGamepad, Flags = Native.RIDEV_REMOVE },
        };
        Native.RegisterRawInputDevices(regs, (uint)regs.Length, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>());
        _hwnd = 0;
        foreach (var d in _devices.Values.OfType<Device>().ToArray()) Remove(d.Handle, notify: false);
        _devices.Clear();
        PublishDevices();
    }

    public void Dispose() => Unregister();

    // ---------- Découverte ----------

    private void Enumerate()
    {
        uint count = 0, size = (uint)Marshal.SizeOf<Native.RAWINPUTDEVICELIST>();
        if (Native.GetRawInputDeviceList(null, ref count, size) != 0 || count == 0) return;
        var list = new Native.RAWINPUTDEVICELIST[count];
        if (Native.GetRawInputDeviceList(list, ref count, size) == uint.MaxValue) return;
        foreach (var item in list.Take((int)count))
            if (item.Type == Native.RIM_TYPEHID) GetDevice(item.Device);
        PublishDevices();
    }

    private Device? GetDevice(nint handle)
    {
        if (_devices.TryGetValue(handle, out var known)) return known;
        var device = TryCreate(handle);
        _devices[handle] = device;
        if (device is not null) _log($"Joystick détecté : {device.Name} ({device.Id}).");
        return device;
    }

    private static Device? TryCreate(nint handle)
    {
        string path = ReadDeviceName(handle);
        if (path.Length == 0 || path.Contains("IG_", StringComparison.OrdinalIgnoreCase)) return null; // XInput : traité à part

        // Données préanalysées du descripteur HID (nécessaires à HidP_*).
        uint size = 0;
        if (Native.GetRawInputDeviceInfoW(handle, Native.RIDI_PREPARSEDDATA, 0, ref size) != 0 || size == 0) return null;
        nint pre = Marshal.AllocHGlobal((int)size);
        bool keep = false;
        try
        {
            if (Native.GetRawInputDeviceInfoW(handle, Native.RIDI_PREPARSEDDATA, pre, ref size) == uint.MaxValue) return null;
            if (Native.HidP_GetCaps(pre, out var caps) != Native.HIDP_STATUS_SUCCESS) return null;
            if (caps.UsagePage != PageGenericDesktop || caps.Usage is not (UsageJoystick or UsageGamepad)) return null;

            var axes = new List<Axis>();
            (int, int)? hat = null;
            ushort n = caps.NumberInputValueCaps;
            if (n > 0)
            {
                var vcaps = new Native.HIDP_VALUE_CAPS[n];
                if (Native.HidP_GetValueCaps(Native.HidP_Input, vcaps, ref n, pre) == Native.HIDP_STATUS_SUCCESS)
                    foreach (var c in vcaps.Take(n).Where(c => c.UsagePage == PageGenericDesktop))
                    {
                        ushort last = c.IsRange != 0 ? c.UsageMax : c.UsageMin;
                        for (ushort u = c.UsageMin; u <= last; u++)
                        {
                            if (u == UsageHat) hat = (c.LogicalMin, c.LogicalMax);
                            else if (u is >= 0x30 and <= 0x35 && c.LogicalMax > c.LogicalMin)
                                axes.Add(new Axis { Usage = u, Min = c.LogicalMin, Max = c.LogicalMax, BitSize = c.BitSize });
                        }
                    }
            }

            var (vid, pid) = HidIds.Parse(path);
            var d = new Device
            {
                Handle = handle, Path = path, Id = $"{vid}_{pid}", Preparsed = pre,
                ReportLength = caps.InputReportByteLength,
                MaxButtons = Native.HidP_MaxUsageListLength(Native.HidP_Input, PageButton, pre),
                Axes = [.. axes], Hat = hat,
                Switch = SwitchProDecoder.Supports(vid, pid) ? new SwitchProDecoder($"{vid}_{pid}") : null,
            };
            d.Name = ReadProductName(path) is { Length: > 0 } name ? name : $"Joystick {d.Id}";
            keep = true;
            return d;
        }
        finally { if (!keep) Marshal.FreeHGlobal(pre); }
    }

    private static string ReadDeviceName(nint handle)
    {
        uint chars = 0;
        Native.GetRawInputDeviceInfoW(handle, Native.RIDI_DEVICENAME, 0, ref chars);
        if (chars == 0) return "";
        nint buf = Marshal.AllocHGlobal((int)chars * 2);
        try
        {
            return Native.GetRawInputDeviceInfoW(handle, Native.RIDI_DEVICENAME, buf, ref chars) == uint.MaxValue
                ? "" : Marshal.PtrToStringUni(buf) ?? "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string ReadProductName(string path)
    {
        nint h = Native.CreateFileW(path, 0, 3 /* lecture+écriture partagées */, 0, 3 /* OPEN_EXISTING */, 0, 0);
        if (h == Native.INVALID_HANDLE_VALUE || h == 0) return "";
        try
        {
            var buf = new char[126];
            return Native.HidD_GetProductString(h, buf, (uint)buf.Length * 2) ? new string(buf).TrimEnd('\0').Trim() : "";
        }
        finally { Native.CloseHandle(h); }
    }

    // ---------- Messages ----------

    /// <summary>WM_INPUT_DEVICE_CHANGE : branchement ou retrait à chaud.</summary>
    public void OnDeviceChange(nint wParam, nint lParam)
    {
        // GIDC_ARRIVAL : un handle déjà connu est d'abord retiré proprement (données préanalysées libérées, touches relâchées).
        if (wParam == 1) { Remove(lParam, notify: false); _devices.Remove(lParam); GetDevice(lParam); PublishDevices(); }
        else if (wParam == 2) Remove(lParam, notify: true);                                  // GIDC_REMOVAL
    }

    private void Remove(nint handle, bool notify)
    {
        if (!_devices.Remove(handle, out var d) || d is null) return;
        var events = _aggregator.RemoveSource(d.Source); // relâche ce qui était tenu
        if (events.Count > 0) _enqueue(events);
        if (d.Preparsed != 0) Marshal.FreeHGlobal(d.Preparsed);
        if (notify) PublishDevices();
    }

    /// <summary>WM_INPUT : un ou plusieurs rapports HID d'un joystick.</summary>
    public void OnInput(nint lParam)
    {
        uint headerSize = (uint)Marshal.SizeOf<Native.RAWINPUTHEADER>();
        uint size = 0;
        if (Native.GetRawInputData(lParam, Native.RID_INPUT, 0, ref size, headerSize) != 0 || size == 0) return;
        nint buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.GetRawInputData(lParam, Native.RID_INPUT, buf, ref size, headerSize) == uint.MaxValue) return;
            var header = Marshal.PtrToStructure<Native.RAWINPUTHEADER>(buf);
            if (header.Type != Native.RIM_TYPEHID) return;
            if (GetDevice(header.Device) is not { } dev) return;

            int sizeHid = Marshal.ReadInt32(buf, (int)headerSize);
            int count = Marshal.ReadInt32(buf, (int)headerSize + 4);
            if (sizeHid <= 0 || count <= 0) return;
            if ((long)headerSize + 8 + (long)count * sizeHid > size) return; // rapport incohérent : on ne lit pas hors du tampon
            for (int i = 0; i < count; i++)
            {
                if (dev.Report.Length != sizeHid) dev.Report = new byte[sizeHid];
                var report = dev.Report;
                Marshal.Copy(buf + (int)headerSize + 8 + i * sizeHid, report, 0, sizeHid);
                bool nintendo = dev.Switch is not null && SwitchProDecoder.CanDecode(report);
                if (!dev.FirstReportLogged)
                {
                    dev.FirstReportLogged = true;
                    _log($"{dev.Name} : premier rapport reçu (id 0x{report[0]:X2}, {report.Length} octets), " +
                        (nintendo ? "décodage Nintendo." : "décodage HID générique."));
                }
                var pressed = nintendo ? dev.Switch!.Decode(report) : Decode(dev, report);
                var events = _aggregator.Replace(dev.Source, pressed);
                if (events.Count > 0) _enqueue(events);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static IReadOnlySet<ButtonKey> Decode(Device d, byte[] report)
    {
        var pressed = new HashSet<ButtonKey>();

        // Boutons (page 0x09) : HidP_GetUsages renvoie ceux qui sont enfoncés. Échec (rapport d'un autre identifiant, qui ne
        // porte pas les boutons) : on garde l'état précédent, sinon les boutons tenus clignoteraient appuyé/relâché.
        if (d.MaxButtons > 0)
        {
            if (d.Usages.Length != d.MaxButtons) d.Usages = new ushort[d.MaxButtons];
            uint len = d.MaxButtons;
            if (Native.HidP_GetUsages(Native.HidP_Input, PageButton, 0, d.Usages, ref len, d.Preparsed, report, (uint)report.Length)
                == Native.HIDP_STATUS_SUCCESS)
            {
                d.LastButtons.Clear();
                for (int i = 0; i < len; i++) d.LastButtons.Add(d.Button(d.Usages[i]));
            }
            pressed.UnionWith(d.LastButtons);
        }

        // Croix directionnelle (même règle).
        if (d.Hat is { } hat)
        {
            if (Native.HidP_GetUsageValue(Native.HidP_Input, PageGenericDesktop, 0, UsageHat, out var raw,
                    d.Preparsed, report, (uint)report.Length) == Native.HIDP_STATUS_SUCCESS)
            {
                d.LastHat.Clear();
                foreach (var dir in HidMapping.HatDirections(HidMapping.HatIndex((int)raw, hat.Min, hat.Max)))
                    d.LastHat.Add(d.Named(dir));
            }
            pressed.UnionWith(d.LastHat);
        }

        // Axes analogiques → directions numériques avec hystérésis.
        foreach (var a in d.Axes)
        {
            if (Native.HidP_GetUsageValue(Native.HidP_Input, PageGenericDesktop, 0, a.Usage, out var v,
                    d.Preparsed, report, (uint)report.Length) != Native.HIDP_STATUS_SUCCESS)
            {
                // Axe absent de ce rapport : sa dernière direction reste valable.
                if (!a.Ignored && a.Direction != 0) pressed.Add(d.Named(a.Name + (a.Direction > 0 ? "Plus" : "Minus")));
                continue;
            }
            long value = v;
            if (a.Min < 0 && a.BitSize is > 0 and < 32 && (v & (1u << (a.BitSize - 1))) != 0) value = (int)(v | (~0u << a.BitSize));
            double norm = Math.Clamp((value - a.Min) / (double)(a.Max - a.Min), 0, 1);

            if (!a.Calibrated)
            {   // premier rapport : un axe qui ne repose pas près du centre est une glissière/manette des gaz, on l'ignore
                a.Calibrated = true;
                a.Ignored = norm is < 0.3 or > 0.7;
            }
            if (a.Ignored) continue;
            a.Direction = HidMapping.AxisDirection(norm, a.Direction);
            if (a.Direction != 0) pressed.Add(d.Named(a.Name + (a.Direction > 0 ? "Plus" : "Minus")));
        }
        return pressed;
    }

    private void PublishDevices()
    {
        _snapshot = _devices.Values.OfType<Device>()
            .Select(d => new LocalInputDevice(d.Source, d.Name, LocalInputKind.Joystick, false)).ToArray();
        _devicesChanged();
    }
}
