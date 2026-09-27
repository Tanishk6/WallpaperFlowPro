using WallpaperFlow.Core.Desktop;
using WallpaperFlow.Core.Native;

namespace WallpaperFlow.Core.Detection;

public sealed record PowerState(bool OnBattery, bool HasBattery, int Percent, bool EnergySaver, bool DisplayOn, bool Suspended, bool Locked);

/// <summary>Battery / AC / energy saver / display / sleep / lock state from power notifications (no polling).</summary>
public sealed class PowerMonitor
{
    private readonly object _gate = new();
    public PowerState Current { get; private set; }
    public event Action<PowerState>? Changed;

    public PowerMonitor(SystemEventsHub hub)
    {
        Current = Read(true, false, false);
        hub.PowerStatusChanged += () => Update(s => Read(s.DisplayOn, s.Suspended, s.Locked));
        hub.PowerSettingChanged += (guid, data) =>
        {
            if (guid == SystemEventsHub.GUID_CONSOLE_DISPLAY_STATE) Update(s => s with { DisplayOn = data != 0 });
            else Update(s => Read(s.DisplayOn, s.Suspended, s.Locked) with
            {
                EnergySaver = guid == SystemEventsHub.GUID_ENERGY_SAVER_STATUS ? data != 0 : Read(s.DisplayOn, s.Suspended, s.Locked).EnergySaver
            });
        };
        hub.Suspending += () => Update(s => s with { Suspended = true });
        hub.Resumed += () => Update(s => Read(true, false, s.Locked));
        hub.SessionLockChanged += locked => Update(s => s with { Locked = locked });
    }

    private void Update(Func<PowerState, PowerState> f)
    {
        PowerState next;
        lock (_gate)
        {
            next = f(Current);
            if (next == Current) return;
            Current = next;
        }
        Changed?.Invoke(next);
    }

    public static PowerState Read(bool displayOn, bool suspended, bool locked)
    {
        if (!Win32.GetSystemPowerStatus(out var st)) return new PowerState(false, false, 100, false, displayOn, suspended, locked);
        bool hasBattery = (st.BatteryFlag & 128) == 0 && st.BatteryFlag != 255;
        bool onBattery = hasBattery && st.ACLineStatus == 0;
        int pct = st.BatteryLifePercent == 255 ? 100 : st.BatteryLifePercent;
        bool saver = (st.SystemStatusFlag & 1) != 0;
        return new PowerState(onBattery, hasBattery, pct, saver, displayOn, suspended, locked);
    }
}
