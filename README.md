# DeskFader

DeskFader is a Windows desktop controller that maps one motorized physical fader to six application volume slots. Select an app with a hardware button, move the fader, and DeskFader updates that app's active Windows audio sessions.

It is designed to stay out of the way: close or minimize the window to keep it running in the notification area, then restore it when you want to change mappings.

## Features

- controls six fixed application-volume slots from one motorized fader
- shows each slot's current target volume and physical-button selection
- discovers applications with active Core Audio sessions
- supports executable browsing for applications that are currently silent
- retains mappings and desired volumes under `%LOCALAPPDATA%\DeskFader`
- starts with Windows through a per-user Run-key entry
- uses a bounded JSON serial protocol with reconnect handling and motor safety

## Main Workflow

1. Press one of the six physical buttons to select a slot.
2. The matching DeskFader channel is highlighted in the dashboard.
3. Move the fader to set that slot's target volume.
4. DeskFader applies the target to every active Core Audio session for the mapped executable.
5. Open **Settings** in the app to map, clear, or browse for an executable.

Applications that are not currently playing audio keep their desired volume. DeskFader applies it once an active session appears.

## Build and Install

DeskFader currently builds from source and requires the .NET 8 SDK.

1. Install the SDK if needed:

```powershell
winget install Microsoft.DotNet.SDK.8
```

2. Build the Windows app from the repository root:

```powershell
dotnet build Windows\DeskFader.sln -c Release
```

3. Launch it:

```powershell
Start-Process .\Windows\DeskFader.Settings\bin\Release\net8.0-windows\DeskFader.Settings.exe
```

The application keeps running when its window is closed or minimized. Use the DeskFader notification-area icon to **Restore** the window or choose **Exit** to stop the controller completely.

## Hardware Setup

DeskFader expects a CircuitPython Feather with the existing motorized-fader wiring.

1. Copy `Feather\boot.py` and `Feather\code.py` to the Feather's `CIRCUITPY` drive.
2. Reboot the Feather after copying the files.
3. Launch DeskFader and connect the Feather by USB.
4. DeskFader scans serial ports, pairs only with a valid DeskFader device, and sends the current six target volumes.

The Feather exposes two serial endpoints:

- **Data port:** DeskFader's JSON protocol. Select this port only for the Windows app.
- **DEBUG console:** firmware diagnostics for a serial monitor. Its output never enters the controller protocol.

## Calibration and Safety

Set `ADC_MIN` and `ADC_MAX` in `Feather\code.py` from raw readings at the safe mechanical endpoints. The firmware clamps the physical fader to 0 through 100.

Important behavior:

- touch on A3 immediately stops the motor and enables manual adjustment
- the motor only drives after valid host state is received
- motor movement has a deadband and timeout to avoid hunting and prolonged stalls
- loss of the CDC data connection stops motor drive
- button selection never changes an application volume by itself

The retained pin map is A0 for the fader, A3 for touch, SDA/SCL for motor direction, and D5/D6/D9/D10/D11/D12 for the six buttons.

## Settings

Settings are stored outside the repository at:

```text
%LOCALAPPDATA%\DeskFader\settings.json
```

The bundled `Windows\controller_config.json` file is used only on first run as a seed. Rebuilding or replacing `DeskFader.Settings.exe` does not overwrite mappings or target volumes.

Physical fader changes are saved at most once per second and are flushed when DeskFader exits normally. Mapping and start-at-login changes save immediately.

Slot order is always the physical button order. A slot may be unmapped, but mapped executable names must be unique case-insensitively.

## Protocol Notes

The Feather and Windows app use newline-delimited UTF-8 JSON frames. Firmware frames are limited to 256 bytes and host frames to 512 bytes.

Messages include:

- device `hello`
- host `state` with six target volumes
- device `ack` for host state
- device `volume` for physical fader changes
- device `select` for physical button selection
- device `error` for rejected frames

The host is authoritative after pairing. The Feather reports its selected slot after each accepted host state so the dashboard restores selection after an app reconnect.

## Tests

Run the C# protocol, settings, and service contract tests from the repository root:

```powershell
dotnet test Windows\DeskFader.sln -c Release
```
