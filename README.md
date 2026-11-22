# MSFS Auto Unpause

MSFS Auto Unpause resumes your flight when Microsoft Flight Simulator 2024 pauses because of a lost internet connection ("Connection to network lost"). It runs in the system tray, waits until your connection is back and stable, then closes the pause menu for you. Pauses you make yourself, including Pause at Top of Descent, are left alone.

**Download: [flightsim.to/addon/119750/msfs-auto-unpause](https://flightsim.to/addon/119750/msfs-auto-unpause)**

This repository is the source code. If you just want to use the app, get it from Flightsim.to.

## Install

1. Unzip the archive to any folder. Don't put it into your Community folder.
2. Start `MSFSAutoUnpause.exe`. If Windows SmartScreen appears, click "More info" and then "Run anyway" (the exe isn't code-signed).
3. Load a flight. Monitoring starts automatically.

No .NET install or admin rights needed. Settings and logs are stored in `%APPDATA%\MSFSAutoUnpause\`.

## How it works

1. **Watches the sim's pause state over SimConnect.** SimConnect is local, so it keeps working while the internet is down.
2. **Checks whether the internet caused the pause.** It does a DNS lookup and a TCP connect to `self.events.data.microsoft.com`, the endpoint MSFS itself checks. If the network was fine, the pause is treated as manual and ignored. A 3-minute look-back catches pauses where MSFS reacts a minute or more after the drop.
3. **Waits for the connection to be stable.** 5 seconds after short outages, 20 seconds with re-checks every 3 seconds after outages longer than 2 minutes.
4. **Closes the pause menu with real input.** It brings MSFS to the front and sends Escape with `SendInput`. From the 3rd attempt it finds the "Resume" button with Windows OCR and clicks it. No input is sent unless MSFS is confirmed to be the active window.
5. **Watchdog.** A heartbeat every 5 minutes, stale-data detection and a UI-thread check, so a freeze always leaves a trace in the log.

## Bug reports

Open an issue and attach the log file from `%APPDATA%\MSFSAutoUnpause\logs\` for the day it happened. Log file names use the UTC date.

## Building

Requirements: Windows 10/11 x64 and the .NET 6 SDK.

```powershell
# Dev build
dotnet build MSFSAutoUnpause\MSFSAutoUnpause.csproj -c Release

# Release zip -> release\MSFSAutoUnpause-v<version>.zip
.\publish.ps1            # single self-contained exe + SimConnect DLLs (6 files)
.\publish.ps1 -Folder    # fallback: exe + ~500 runtime DLLs in one folder
```

The SimConnect DLLs are kept next to the exe instead of inside it. `Microsoft.FlightSimulator.SimConnect.dll` is a mixed-mode (C++/CLI) assembly and can't be loaded from a single-file bundle. The `KeepSimConnectOutsideBundle` target in the csproj handles this.

### Releasing a new version

1. Bump `<Version>` in `MSFSAutoUnpause/MSFSAutoUnpause.csproj`.
2. Add an entry to `CHANGELOG.md`.
3. Run `.\publish.ps1` and note the SHA256 it prints.
4. Test the exe from `release\MSFSAutoUnpause\` on a real flight.
5. Upload the zip as a new version of the existing Flightsim.to listing and update the SHA256 in the description.

## Project layout

| Path | Purpose |
|---|---|
| `MSFSAutoUnpause/MainForm.cs` | SimConnect, pause detection, focus and input automation, watchdog, UI |
| `MSFSAutoUnpause/NetworkMonitor.cs` | DNS and TCP internet checks |
| `MSFSAutoUnpause/PortDiagnostics.cs` | Detects Windows port exhaustion so it can't trigger a false unpause |
| `MSFSAutoUnpause/AppSettings.cs` | Settings in `%APPDATA%\MSFSAutoUnpause\settings.json` |
| `MSFSAutoUnpause/Logger.cs` | Daily logs in `%APPDATA%\MSFSAutoUnpause\logs\`, kept for 2 days |
| `MSFSAutoUnpause/SimConnect/` | SimConnect DLLs from the MSFS SDK |
| `publish.ps1` | Builds the release zip |
| `README.txt` | User readme that ships inside the zip |
| `FLIGHTSIM_TO_LISTING.md` | Flightsim.to description and changelog text |

## License

MIT, see [LICENSE](LICENSE). The SimConnect DLLs are part of the Microsoft Flight Simulator SDK, belong to Microsoft and aren't covered by this license.
