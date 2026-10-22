# INZONE Mic Switch

Switch the Windows default microphone between Sony INZONE Buds and a desk microphone using the earbuds' touch controls.

Raise the earbuds volume one step, then lower it one step within four seconds. The bridge detects that pattern and switches microphones. It can start silently at Windows sign-in, so you do not need to keep a terminal open.

Independent, experimental Windows utility. No Sony software or firmware is included. See [LICENSE](LICENSE) (MIT) and [NOTICE](NOTICE).

## Requirements

- Windows 11 x64 is the tested platform. Other Windows versions and architectures are untested.
- INZONE Buds connected through their USB transceiver in **PC mode**. The implemented HID interface is `054C:0EC2`, `MI_05`, `COL03`; other INZONE models and Bluetooth are unsupported.
- A second microphone. The default name match is `Yeti`, but you can select any active Windows capture endpoint.
- [.NET 9 x64 Runtime](https://learn.microsoft.com/en-us/dotnet/core/install/windows) to run the release; the .NET 9 SDK to build it. The SDK also includes the runtime. Install the **.NET Runtime**, not only the ASP.NET Core runtime.

## Get started

1. Download `InzoneMicSwitch-win-x64.zip` from this repository's Releases, when available, and extract it. GitHub's **Source code** ZIP must be built first; see below.
2. Keep the Buds microphone unmuted. Set the input device in Discord, Teams, games, or other calling apps to **Default**. Apps pinned to a specific microphone will keep using it.
3. For a Yeti desk microphone, double-click `Preview-MicSwitch.cmd`. This displays the gesture it would apply without changing Windows audio defaults.
4. Lower the earbuds volume once to establish a baseline. Then raise it exactly one step and lower it exactly one step within four seconds. Look for `PREVIEW would select ...`. Wait at least two seconds before repeating.
5. Close preview with Ctrl+C. Double-click `Start-MicSwitch.cmd` to enable switching, or use the startup instructions below.

Volume still changes normally. Any matching up/down pattern can trigger a switch, including an ordinary volume adjustment. The first received volume event after startup or reconnect only establishes the baseline. If the bridge knows the Buds mic is hardware-muted, it skips switches to the Buds.

### Choose another desk microphone

Open PowerShell in the extracted release folder:

```powershell
.\dist\InzoneMicSwitch.exe --list
.\dist\InzoneMicSwitch.exe --desk-name "Microphone name from the list"
```

Name matching is case-insensitive and accepts a substring. If multiple microphones match, use their exact IDs:

```powershell
.\dist\InzoneMicSwitch.exe --desk-id "ID copied from --list" --enable-switch
```

`--buds-name` and `--buds-id` are also available, for example with localized Windows device names. Exact IDs take precedence over names. Both endpoints must be active and different. Only one bridge instance can run in your Windows session; disable autostart before running a manual preview.

## Start automatically at sign-in

For the default Yeti/INZONE Buds pairing, double-click `Enable-Autostart.cmd`. For another microphone, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-Autostart.ps1 -DeskName "Your microphone"
```

The installer accepts `-DeskName`, `-BudsName`, `-DeskId`, `-BudsId`, `-WindowMs` (default 4000), `-Gesture` (default `volume-return`), and `-NoStart` (install without starting immediately).

It copies the background app into `%LOCALAPPDATA%\InzoneMicSwitch\app` and registers the current user's **INZONE Microphone Switcher** scheduled task. It starts at sign-in with normal user permissions, runs without a window, waits for missing devices, and reconnects after a disconnect. The task is configured to restart on failure. No administrator account or Windows service is required.

You can move or delete the extracted release folder after installation. Logs are in `%LOCALAPPDATA%\InzoneMicSwitch\logs\bridge.log`, with one rotated backup (approximately 10 MiB combined). The bridge logs device events; it does not record microphone audio.

- **Disable:** double-click `Disable-Autostart.cmd` from a release folder. This stops the installed bridge and disables the task; the current microphone selection remains in place.
- **Update/re-enable:** extract a new release and run the installer again, including your microphone and gesture options. Each installation replaces the task's options; omitted options return to defaults.
- **Remove:** disable first, delete the **INZONE Microphone Switcher** task in Task Scheduler, then delete `%LOCALAPPDATA%\InzoneMicSwitch` if you no longer want the app or logs.

### Does INZONE Hub have to stay open?

The bridge reads the transceiver directly and does not call INZONE Hub. On the development machine, reports continued after Hub exited and a fresh bridge process connected while Hub was closed. A cold boot or freshly replugged transceiver without Hub initialization has **not yet been verified**, nor has a physical gesture with Hub absent. If no reports arrive, open Hub once and retry. Hub may still be useful for assigning the earbuds' volume controls.

## Optional mute gesture

```powershell
.\dist\InzoneMicSwitch.exe --trigger mute-state --enable-switch
```

After the initial mute-state baseline, muting the Buds selects the desk microphone; unmuting selects the Buds. This repurposes the Buds mute control: **muting the Buds can activate the desk microphone**. It is not a global mute button.

## Build from source

Install the .NET 9 SDK, then run in the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
```

The script builds separate console and windowless executables, runs the offline protocol/gesture tests, and produces `artifacts/InzoneMicSwitch-win-x64.zip` plus a SHA-256 file. Extract the ZIP for the launch/install scripts. Build products live under `artifacts/`; the source tree stays clean. `-OutputDirectory` chooses another output folder. Each build uses a fresh staging folder, so old build folders can be removed when no longer needed.

GitHub Actions runs the same build and tests for pushes and pull requests. It uploads the release ZIP as a workflow artifact; creating a public GitHub Release is a separate step. See [publishing instructions](docs/PUBLISHING.md).

## Troubleshooting and limits

- **No gesture:** prime the baseline, use exactly one step up and one down, and check the four-second window. Confirm the transceiver is in PC mode and the touch controls are assigned to volume.
- **Waiting for a microphone:** run `--list`, check the name/ID options, and enable both microphones in Windows. IDs can change after reinstalling devices; name matching is more tolerant.
- **Another bridge instance:** disable the installed task before starting a console instance. `--list`, `--help`, `--self-test`, and offline replay work without stopping it.
- **Windows switched, call did not:** some apps do not follow default-device changes during an active call. Re-select Default input or restart the call. Seamless migration is application-dependent and has not been validated here.
- **Switch failure:** the bridge verifies all three Windows capture roles (console, multimedia, communications) and attempts rollback on failure. Windows switching uses the undocumented `IPolicyConfig` COM interface, which may change in future Windows versions.
- **Hardware coverage:** live tests confirmed volume/mute reports and a volume gesture selecting a Yeti microphone. Direct switching to the Buds also worked. Broad firmware, PC, reconnect, sleep/resume, and two-way physical gesture testing is still needed.

Run `dist\InzoneMicSwitch.exe --help` for all options. `--seconds N` limits a manual session. `--log PATH` captures console reports for diagnosis; `--replay PATH` decodes them offline without accessing devices or changing audio. Review captures before sharing them: diagnostic output can contain local paths and device identifiers.

## Development

See [CONTRIBUTING.md](CONTRIBUTING.md) for validation and [docs/PROTOCOL.md](docs/PROTOCOL.md) for the observed report format. This implementation opens HID for reading only; it sends no device commands and modifies no firmware. Its only intended system change is selecting the Windows default microphone when switching is explicitly enabled.
