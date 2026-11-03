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

## Deliberately not supported

| Feature | Why |
|---|---|
| CPU/GPU die temperature | No documented Windows API. Common tools load a kernel driver (e.g. WinRing0) that Microsoft Defender flags as vulnerable. |
| TDP | OneXPlayer exposes TDP through its own OneXConsole/EC interface, which is not publicly documented for the OneXPlayer 3 (Intel Arc G3 Extreme). Intel platform power limits require privileged MSR/MMIO access. |
| Fan curves | Embedded-controller registers are undocumented; wrong writes can damage hardware. |
| FPS overlay | Requires hooking the game's swap chain or ETW present events with admin rights; out of scope. |
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
