# Observed INZONE Buds reports

These are implementation notes for the supported Buds USB transceiver, not an official Sony specification. Protocol research used INZONE Hub inspection and live reports. This document contains the format needed by the independently authored reader; it does not include Sony code or captured device data.

## Transport

Windows HID interface path contains `vid_054c&pid_0ec2&mi_05&col03`. The reader checks vendor usage page `0xFF04` and a 64-byte input report size. It opens the interface for read access with read/write sharing, allowing other software to continue using the device.

Input report byte 0 is report ID `0x02`; byte 1 is the stream-byte count (at most 62). The remaining counted bytes belong to an HCI event stream. Report padding is ignored. An HCI event can cross HID report boundaries, and a report may hold multiple events.

## Event envelope

The supported vendor HCI event starts with `04 FF length`. Its parameters are:

| Parameter bytes | Meaning |
| --- | --- |
| `00 96 C3` | Vendor prefix; `0xC396` is a header marker, not encryption |
| Address | Source in the low nibble, destination in the high nibble |
| Event ID | State/event selector |
| Event type | `0x10`, `0x20`, or `0xA0` |
| Transaction ID, 2 bytes | Little-endian; repeated IDs are valid |
| Payload | Event-specific data |
| Checksum | Sum of all preceding parameter bytes modulo 256 |

Address `0x14` represents headset (4) to PC (1); `0x12` represents transceiver (2) to PC (1). The reader validates the header, address, type, length, and checksum. A 50-byte event payload can indicate a fragment; the matching sequence completes with a shorter payload. Reassembly storage and fragment lifetime are bounded. See `Protocol.cs` and synthetic tests for exact handling.

## Gesture inputs

Only headset-to-PC reports drive gestures. Relevant payloads have three bytes:

| Event ID | Used fields |
| --- | --- |
| `0x21` | Byte 0: headphone mute; byte 1: volume (`0xFF` ignored) |
| `0x24` | Byte 0: microphone muted, `0` or `1` |

Only type `0xA0` changes trigger an action. Other accepted event types can establish state without switching. A transceiver event ID `0x01` resets the gesture baseline.

Volume mode detects a rise from V to V+1 followed by a return to V within the configured window (default four seconds), with a two-second toggle cooldown. Mute mode maps a changed mute state to desk (muted) or Buds (unmuted). The initial state produces no switch. State and partial packets are reset when the reader reconnects.

Transaction IDs are not reliable duplicate-event keys: hardware was observed reusing an ID for distinct state updates. The decoder therefore retains repeated IDs, while gesture logic ignores unchanged volume/mute states.

## Windows audio

Capture endpoints are enumerated through Core Audio. A switch uses `IPolicyConfig.SetDefaultEndpoint` for the console, multimedia, and communications roles, then reads all three back. Failure triggers an attempt to restore the prior defaults. The policy-setting interface is undocumented; application-specific stream migration is outside the bridge's control.

## Scope

The reader sends no HID commands, polls no Sony process, and performs no firmware modification. Whether a device needs prior initialization by Hub after a cold connection remains unverified. Other headset models, report formats, and Bluetooth are outside the implemented scope.
