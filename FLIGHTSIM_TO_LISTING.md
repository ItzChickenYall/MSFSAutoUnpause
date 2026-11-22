# Flightsim.to listing draft

Paste these into the upload form. Category: **Tools / Utilities**. Compatibility: **MSFS 2024**.

## Title
MSFS Auto Unpause: resume after internet drops

## Short description
Automatically resumes your flight after an internet drop pauses MSFS 2024. Made for long-haul and overnight flights.

## Description

Ever left a long-haul running overnight and woken up to find MSFS paused over the Atlantic with "Connection to network lost", six hours of flight time wasted?

**MSFS Auto Unpause** is a small tray app that watches for exactly that. When your internet comes back, it brings MSFS to the front and closes the pause menu for you, so your flight carries on.

**Features**
- Only acts on network-caused pauses. Your own Escape pauses and Pause-at-TOD are left alone.
- Waits until your connection is stable before resuming, not just the first moment it comes back.
- Uses Escape like you would, and falls back to finding and clicking the "Resume" button.
- Runs in the system tray. Optional start with Windows.
- No install and no runtime to download: unzip and run.
- Detailed logs if anything goes wrong.
- Free and open source (MIT), with the full source on GitHub.

**Installation**
1. Unzip the `MSFSAutoUnpause` folder anywhere (NOT the Community folder, since this is a standalone app).
2. Run `MSFSAutoUnpause.exe`. If Windows SmartScreen appears, click *More info → Run anyway* (the app isn't code-signed).
3. Load a flight. Monitoring starts automatically.

**Good to know**
- To resume, the app focuses the MSFS window and sends a real key press or click. If you're using the PC at that moment, MSFS will pop to the front.
- Some antivirus programs are suspicious of apps that send key presses or take screenshots (the app reads the screen to find the "Resume" button). The source code is public if you want to check exactly what it does.

**Source and bug reports:** https://github.com/<your-username>/MSFSAutoUnpause

## Changelog for this version
Copy the 1.1.0 section from CHANGELOG.md.

## Screenshots to take (Flightsim.to needs at least one image)
1. The main window showing "Monitoring" during a flight.
2. The main window on "Waiting to reconnect…" or "Reconnected · resuming in Xs…".
3. The Settings window.
4. Optional: the tray icon menu.

## Before uploading
- [ ] Bump `<Version>` in the csproj and date the CHANGELOG entry
- [ ] Run `.\publish.ps1`
- [ ] Unzip the result to a fresh folder and test a real flight with that copy
- [ ] Upload the zip to virustotal.com and check the result
- [ ] Create a GitHub Release (tag `v1.1.0`) and attach the zip
- [ ] Upload the same zip to Flightsim.to
