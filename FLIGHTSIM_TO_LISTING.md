# Flightsim.to listing draft

Paste these into the upload form. Category: **Tools / Utilities**. Compatibility: **MSFS 2024**.

## Title
MSFS Auto Unpause

## Short description
Automatically unpauses MSFS 2024 when an internet drop pauses your flight. Built for long-haul and overnight flights while you're AFK.

## Description

MSFS Auto Unpause resumes your flight when MSFS 2024 pauses because of a lost internet connection ("Connection to network lost"). It runs in the system tray, waits until your connection is back and stable, then closes the pause menu for you.

Pauses you make yourself, including Pause at Top of Descent, are left alone.

Features

* Only reacts to pauses caused by an internet drop
* Waits until the connection is stable again before unpausing (longer after long outages)
* Also catches the case where MSFS pauses a minute or more after the drop
* Unpauses with Escape, and clicks "Resume" on screen if Escape doesn't work
* Only sends input when MSFS is the active window
* Runs in the system tray, optional start with Windows
* Logs every pause and unpause to %APPDATA%\MSFSAutoUnpause\logs
* No installation, no .NET download, no admin rights needed

How to use

* Unzip the archive to any folder. Don't put it into your Community folder.
* Start `MSFSAutoUnpause.exe`.
* Load a flight. Monitoring starts automatically.

Good to know

* Tested with MSFS 2024 only.
* To unpause, the app brings MSFS to the front. If you're using the PC at that moment, MSFS will jump in front of your other windows.
* The program isn't code-signed. If Windows SmartScreen appears, click "More info" and then "Run anyway".
* Some antivirus programs may flag it because it sends key presses and reads the screen to find the "Resume" button. The source code is public.
* If it didn't unpause when it should have, please attach your log file from %APPDATA%\MSFSAutoUnpause\logs to your comment or GitHub issue.
* SHA256 of `MSFSAutoUnpause.exe`:
* E12C44858FAF453D3EABD18D6A80ABEA03950D514808EBE6E7A73B0DDB0D3E4F
* Source code: [github.com/ItzChickenYall/MSFSAutoUnpause](https://github.com/ItzChickenYall/MSFSAutoUnpause)

## Changelog for this version
1.1.0
- Fixed: unpausing now works in MSFS 2024 (it presses Escape, or clicks Resume if needed)
- Waits until your internet is stable before unpausing
- Also catches pauses that happen a minute or more after the drop
- Only sends input when MSFS is the active window
- Now a single exe, no .NET install needed
- New icon, version number shown in the app

## Images (in release\fsto-images\)
1. `cover.jpg`: main image
2. `main_inflight.png`: the app window during a flight
3. `icon.png`: thumbnail/logo if the form asks for one

## Before uploading
- [ ] Bump `<Version>` in the csproj and date the CHANGELOG entry
- [ ] Run `.\publish.ps1`
- [ ] Test a real flight with the new build
- [ ] Upload the zip to virustotal.com and check the result
- [ ] Upload the zip to Flightsim.to
- [ ] Optional: create a GitHub Release (tag `v1.1.0`) and attach the same zip
