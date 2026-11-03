# Hardware abstraction layer

## Principles

1. **One interface per capability** (`IVolumeControl`, `IBrightnessControl`, `IDisplayModeControl`,
   `IPowerPlanControl`, `IBatteryInfo`, `ISystemMonitor`, `ITdpControl`, `IFanControl`).
2. **Providers use documented Windows APIs only.** If a call fails, the provider is not exposed.
3. **Device profiles only permit.** A profile (matched from SMBIOS strings) lists the features it
   allows. `HardwareComposer` exposes a provider only if it exists **and** the profile permits it.
4. **Nothing is inferred from the model name.** A OneXPlayer profile does not "turn on" TDP or fan
   control – there is no verified backend, so none ships, and no profile permits them.
5. The UI hides unsupported controls and shows the profile's explanation (Settings → Device).

## What works today

| Feature | Provider | API | Notes |
|---|---|---|---|
| Battery | `Win32BatteryInfo` | `GetSystemPowerStatus` | %, AC/battery, charging, battery saver, time left |
| Volume / mute | `CoreAudioVolumeControl` | Core Audio `IAudioEndpointVolume` | follows default output device |
| Brightness | `WmiBrightnessControl` | WMI `WmiMonitorBrightness(Methods)` | internal panel only; hidden if not supported |
| Refresh rate | `DisplayModeControl` | `EnumDisplaySettings` / `ChangeDisplaySettingsEx` | lists modes at current resolution; tests with `CDS_TEST` first |
| Power plan | `PowerPlanControl` | `PowerEnumerate` / `PowerSetActiveScheme` | classic plans; Windows 11's *power mode* slider has no supported public setter, so Lumen links to Settings |
| CPU % | `WindowsSystemMonitor` | `GetSystemTimes` | |
| GPU % | `WindowsSystemMonitor` | "GPU Engine" perf counters (3D) | same source as Task Manager; Intel Arc supported via WDDM |
| RAM | `WindowsSystemMonitor` | `GlobalMemoryStatusEx` | |
| Temperature | `WindowsSystemMonitor` | "Thermal Zone Information" perf counters | **ACPI zone, approximate** – labelled as such, not CPU die temp |

## Real TDP control (Intel, optional)

| | |
|---|---|
| Registers | `MSR_RAPL_POWER_UNIT 0x606`, `MSR_PKG_POWER_LIMIT 0x610` (PL1/PL2), `MSR_PKG_ENERGY_STATUS 0x611` (measured package power), `MSR_PKG_POWER_INFO 0x614` (CPU min/max), package temperature `0x1B1`/`0x1A2` – Intel SDM Vol. 3B |
| Driver | [PawnIO](https://pawnio.eu) (signed, open source, Memory Integrity compatible) + its signed `IntelMSR.bin` module, which only allows writing `MSR_PKG_POWER_LIMIT` among power registers. Installed by the user; Lumen never installs drivers. |
| Elevation | `Lumen.PowerHelper.exe`, started on demand by the scheduled task `Lumen\PowerHelper` (created/removed in Settings with one UAC prompt each). Named pipe restricted to your account; every request validated on the elevated side. |
| Range | From MSR 0x614 (CPU max/min) or the firmware's own boot-time PL1/PL2. Never hard-coded. Floor 5 W when the CPU reports no minimum. |
| Presets | Eco 10 W, Balanced 20 W, Performance 28 W, Turbo 35 W – shown disabled, with the reason, when outside the range. |
| Writes | PL1 = PL2 = target (steady handheld-style limit). Time windows, clamp bits and the **lock bit are preserved**; locked limits are read-only. |
| Verification | Value read back after every write; measured package power shown live; if firmware (Intel DTT) keeps resetting the limit Lumen stops after 3 re-applies and says so. |
| Restore | Firmware value saved per boot and restored on "Firmware default", when a game profile ends, when Lumen stops talking to the helper for 2 minutes (crash), on removal, and by any reboot. |
| AMD | HAL slot exists (`ITdpBackend`); no AMD backend ships in this version. |

Not verified on a OneXPlayer 3 yet: whether its firmware locks or overrides `MSR_PKG_POWER_LIMIT`.
Settings → Performance shows the live result after setup.

## Smart Power Control

One slider (0 % battery … 50 % balanced … 100 % performance) computed by `SmartPower` (unit-tested):
TDP 8 → 20 → 35 W clamped to the CPU range, refresh rate ~60 / ~90 / max (one step lower on battery
at the top), Windows power plan saver / balanced / high performance when those plans exist, and an
*advisory* FPS target for the game's own limiter. Runtime estimates use the measured battery discharge
(Windows battery report) and, when previewing a lower TDP, the measured package power. Separate battery
and AC positions; optional automatic switching when the power source changes (not while a game
profile is active).

## Deliberately not supported

| Feature | Why |
|---|---|
| CPU/GPU die temperature | No documented Windows API. Common tools load a kernel driver (e.g. WinRing0) that Microsoft Defender flags as vulnerable. |
| Fan curves | Embedded-controller registers are undocumented; wrong writes can damage hardware. |
| FPS limiter | No non-invasive universal limiter on Windows/Intel (RTSS etc. inject into games → anti-cheat risk). Use the game's or Intel Graphics Software's limiter; Smart Power suggests a target. |
| FPS telemetry / OSD | Planned via ETW present events in the elevated helper (no injection) – not in this version. |
| Universal game suspend | Windows has no safe, universal mechanism; suspending arbitrary processes breaks online games/anti-cheat. |
| Drawing over exclusive-fullscreen games | The overlay is a normal topmost window; it works over borderless/windowed games. |
| Blocking game input while the overlay is open | XInput is not focus-exclusive; games may still see button presses. |

## OneXPlayer 3 profile

`OneXPlayer3Profile` matches manufacturer `ONE-NETBOOK`/`ONEXPLAYER` and a product or family name
starting with `ONEXPLAYER3` (spaces/punctuation ignored). **The exact SMBIOS strings have not been
verified on a unit.** Open **Settings → Device** on your OneXPlayer 3: it shows the detected
manufacturer/product/board/family and the resolved profile. If it says "OneXPlayer (family)" or
"Generic", send those strings and adjust `OneXPlayer3Profile.Matches`. Functionally nothing changes
either way – all three profiles use the same documented providers; the OX3 profile adds panel
traits (1920×1200, 8.8", 144 Hz, detachable controllers).

## Adding a vendor backend later (TDP/fan)

Only if a documented interface exists (vendor SDK, published WMI/ACPI method, signed driver with a
documented IOCTL):

1. Implement `ITdpControl` / `IFanControl` in `Lumen.Platform.Windows/Hardware/Vendor/…`, with a
   `TryCreate()` that verifies the device and firmware version and returns `null` otherwise.
2. Add the feature to that device profile's `PermittedFeatures`.
3. Pass the provider in `WindowsHardwareFactory` (`ProviderCandidates.Tdp`).
4. Add a unit test that the generic profile still refuses it.
5. Surface it in the quick-access panel behind an explicit "enable experimental control" toggle.
