# MSFS Auto Unpause

A small Windows tray app that automatically resumes **Microsoft Flight Simulator 2024** after an internet drop pauses your flight. It's built for long-haul and overnight flights, so a short internet blip doesn't leave you stuck paused over the ocean for six hours.

**Download:** get the ready-to-run zip from Flightsim.to (link coming soon). This repo is the source code.

## How it works

1. **Watches the sim over SimConnect** (local, so it keeps working while the internet is down) for MSFS's pause state.
2. **Checks whether the network caused the pause.** It runs a DNS lookup plus a real TCP connect to `self.events.data.microsoft.com`, the same endpoint MSFS itself uses. Pauses where the network was fine (Escape, Pause-at-TOD) are marked *manual* and left alone. A 3-minute look-back window catches MSFS's delayed reaction to short blips.
3. **Waits for the internet to come back and stay up.** The settle time is 5 s, or 20 s with active re-checks after outages longer than 2 minutes.
4. **Closes the pause menu with real input.** It forces MSFS to the foreground and sends Escape via `SendInput`. If that fails, it finds the *Resume* button with Windows OCR and clicks it. It never sends input unless MSFS is confirmed to be the foreground window.
5. **Watchdog:** a heartbeat every 5 min, stale-data detection and UI-thread ping logging, so a silent failure leaves evidence in the log.

## Building

Requirements: Windows 10/11 x64 and the .NET SDK that matches `<TargetFramework>` in the csproj.

```powershell
# Dev build
dotnet build MSFSAutoUnpause\MSFSAutoUnpause.csproj -c Release

# Release zip (self-contained, users need nothing installed) -> release\MSFSAutoUnpause-v<version>.zip
.\publish.ps1
```

To release: bump `<Version>` in `MSFSAutoUnpause/MSFSAutoUnpause.csproj`, add an entry to `CHANGELOG.md`, run `publish.ps1`, then upload the zip to a GitHub Release and Flightsim.to.

## Project layout

| Path | Purpose |
|---|---|
| `MSFSAutoUnpause/MainForm.cs` | SimConnect, pause detection state machine, focus and input automation, UI |
| `MSFSAutoUnpause/NetworkMonitor.cs` | DNS and TCP internet checks |
| `MSFSAutoUnpause/PortDiagnostics.cs` | Detects ephemeral-port exhaustion so it never arms a false unpause |
| `MSFSAutoUnpause/AppSettings.cs` | Settings in `%APPDATA%\MSFSAutoUnpause\settings.json` |
| `MSFSAutoUnpause/Logger.cs` | Daily rolling logs in `%APPDATA%\MSFSAutoUnpause\logs\` |
| `MSFSAutoUnpause/SimConnect/` | SimConnect DLLs from the MSFS SDK (Microsoft's, not MIT-licensed) |

## License

MIT, see [LICENSE](LICENSE). The SimConnect DLLs are part of the Microsoft Flight Simulator SDK and are not covered by this license.
