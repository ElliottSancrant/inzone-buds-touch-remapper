# Reprogram the gesture actions

With no configuration file, three noise-canceling/ambient-mode changes within four seconds toggle between the Buds and desk microphones. No file is automatically loaded. Pass `--config` to opt into your own mappings; a configuration file can run programs with your user account's permissions, so review it before enabling actions.

## Example: open an app instead

```json
{
  "version": 1,
  "bindings": {
    "ambient-triple": {
      "type": "launch",
      "fileName": "%SystemRoot%\\System32\\notepad.exe",
      "arguments": []
    }
  }
}
```

Save this as `my-actions.json`, or start from `examples/launch-app.json` in the release. From the release folder:

```powershell
# Check syntax and settings without hardware or side effects.
.\dist\InzoneMicSwitch.exe --config .\my-actions.json --validate-config

# Watch actual gestures and describe their actions without executing them.
.\dist\InzoneMicSwitch.exe --config .\my-actions.json

# Execute the configured actions.
.\dist\InzoneMicSwitch.exe --config .\my-actions.json --enable-actions
```

Close each listener before starting the next. `Start-MicSwitch.cmd --config ...` also enables all configured actions, despite the launcher retaining its original name. `--enable-switch` remains a compatibility alias for `--enable-actions`, including command/launch actions.

## Gestures

| Binding key | Meaning |
| --- | --- |
| `ambient-triple` | Three distinct active ANC/ambient-mode changes within `windowMs`; two-second cooldown |
| `volume-return` | Volume down, up, down, up (one step each), all four changes within `windowMs`; two-second cooldown |
| `mic-muted` | Buds microphone changes from unmuted to muted |
| `mic-unmuted` | Buds microphone changes from muted to unmuted |

The default binding is `ambient-triple`; `volume-return` remains available as an opt-in four-step gesture. All four gestures can be mapped in one file. For `ambient-triple`, the first active notification counts even without an earlier baseline; passive reports do not count, and repeated modes or changes only to ambient level do not advance the count. Volume and mic gestures still need their initial state baseline. Unlisted gestures do nothing. A supplied `bindings` object **replaces** the default mappings; `{}` disables every action. Omitting `bindings` keeps the default microphone toggle. Unknown gesture names, action types, and JSON property names are rejected.

The bridge detects these observable device events; it cannot assign arbitrary taps or suppress the earbuds' normal listening-mode/volume/mute behavior. These controls still operate on the headset even when remapped here. Mapping `mic-muted` to another action does not turn it into a global microphone mute.

## Action types

| Type | Settings and behavior |
| --- | --- |
| `microphone` | `target`: `toggle` (default), `buds`, or `desk`. Sets all three Windows default input roles. |
| `launch` | `fileName`, optional `arguments` array and `workingDirectory`. Starts an executable and leaves it running independently. |
| `command` | Same executable settings, plus `timeoutMs` (default 10000, allowed 100..300000). Waits asynchronously; reports nonzero exit codes. On timeout or graceful bridge shutdown, attempts to terminate the process tree. |
| `none` | Explicitly disable a gesture. |

Only one action runs at a time. During a running command, additional bound gestures are skipped and logged, while HID listening continues. There is no delayed action queue. Launch actions return immediately; repeating the gesture can launch another instance. Launched apps remain open when the bridge stops. Force-stopping the bridge through Task Scheduler or killing its process does not guarantee command cleanup.

Programs are started directly, without an implicit shell. Put each argument in its own JSON string; no extra command-line quoting is needed. `fileName` must name an executable. To run PowerShell, Python, AutoHotkey, or another scripting language, specify its interpreter as `fileName` and the script path as an argument. Shell operators such as `&&` have no special meaning unless you explicitly launch a shell.

`%ENVIRONMENT_VARIABLE%` expansion is supported in the executable, arguments, and working directory. Relative executable paths containing a directory separator resolve against the configuration file's folder. Bare executable names use normal Windows executable lookup. The default working directory is the configuration file's folder; relative script arguments are interpreted by the launched program from there. Normal Windows script execution policies still apply.

The `examples/run-script.json` configuration calls `examples/scripts/Gesture.ps1`, which only prints a message. Replace the script with your own automation. No recompilation is required.

## Microphone and timing settings

```json
{
  "version": 1,
  "windowMs": 4000,
  "microphones": {
    "budsName": "INZONE Buds",
    "deskName": "Yeti"
  },
  "bindings": {
    "ambient-triple": { "type": "microphone", "target": "toggle" },
    "mic-muted": { "type": "none" }
  }
}
```

`windowMs` accepts 250..10000. Names are case-insensitive substring matches; `budsId` and `deskId` optionally select exact IDs from `--list`. CLI microphone options and `--window-ms` override the file. `--trigger` is the legacy preset selector and cannot be combined with `--config`; use bindings instead.

Microphones are resolved when a microphone action runs. A missing or ambiguous endpoint logs a failed action; the listener continues so you can reconnect a microphone and retry. Command-only mappings require no capture devices. Switching to a known hardware-muted Buds input is skipped.

## Keep custom actions enabled at sign-in

Place your configuration and scripts somewhere permanent, then install from an extracted release:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-Autostart.ps1 -Config "C:\YourFolder\my-actions.json"
```

The installer validates the configuration before replacing the scheduled task. It references the config at its original absolute path and **does not copy it or its scripts**. Keep that folder in place. Settings are loaded once at process startup; after editing, restart the **INZONE Microphone Switcher** task or rerun the installer with the same `-Config` path. Include `-Config` again when updating; omitting it restores the default microphone preset.

Preview describes actions without launching programs or setting audio defaults. Offline replay reports gesture names and mapped action types without accessing devices or executing handlers. Configuration validation checks structure and options, not whether programs or devices exist.

## Add a built-in action in C#

The layers are `ProtocolDecoder` -> `GestureDetector` -> `ActionDispatcher` -> `IGestureAction`. Device parsing and gesture detection have no action-specific logic. To add a built-in action, implement `IGestureAction`, add its configuration validation in `BridgeConfiguration`, and register it in `Program.cs`. Respect `ActionContext.Preview` and cancellation. Add tests using fake dependencies; never modify real audio defaults in offline tests.

Process handlers use .NET's [argument list API](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.argumentlist?view=net-9.0) to preserve argument boundaries and [asynchronous process waiting](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.waitforexitasync?view=net-9.0) to keep reading HID reports during commands.
