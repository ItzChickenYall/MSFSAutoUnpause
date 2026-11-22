# Flightsim.to listing draft

Paste these into the upload form. Category: **Tools / Utilities**. Compatibility: **MSFS 2024**.

## Title
MSFS Auto Unpause

## Short description
Gets your flight going again after an internet drop pauses MSFS 2024. Made for overnight long-hauls.

## Description

I fly a lot of long-hauls overnight, and more than once I've woken up to find the sim sat paused somewhere over Greenland with "Connection to network lost" on screen. The internet had dropped for a minute at 3am and MSFS just stopped and waited for me. Hours of flight, gone.

So I made this. It's a little app that sits in your system tray while you fly. If MSFS pauses because your internet dropped, it waits for the connection to come back and then unpauses the sim for you, same as if you'd pressed Escape yourself.

It only steps in when the pause was caused by the internet. If you pause the sim yourself, or you're using Pause at Top of Descent, it leaves it alone.

**How to use it**
1. Unzip the MSFSAutoUnpause folder anywhere you like. It's a normal app, so it doesn't go in your Community folder.
2. Run MSFSAutoUnpause.exe. Windows might show a blue "Windows protected your PC" box because I haven't paid for a code-signing certificate. Click "More info" and then "Run anyway".
3. Load into a flight. That's it, it starts watching on its own.

There's a settings button if you want it to start with Windows. You don't need to install .NET or anything else, everything's in the folder.

**A couple of things to know**
- When it unpauses, it brings MSFS to the front and presses Escape (or clicks Resume if Escape doesn't do it). If you happen to be using the PC at that moment, MSFS will jump in front of whatever you're doing.
- Because it presses keys and reads the screen to find the Resume button, the odd antivirus might get suspicious. It's open source, so you can see exactly what it does.

It's free and the code's on GitHub: https://github.com/ItzChickenYall/MSFSAutoUnpause

If it doesn't unpause when it should, leave a comment or open an issue on GitHub and attach your log file from %APPDATA%\MSFSAutoUnpause\logs. That's the quickest way for me to work out what happened.

## Changelog for this version
1.1.0
- Unpausing actually works now. The old version used a SimConnect command that MSFS 2024 quietly ignores.
- Waits for your internet to properly settle before unpausing, so it doesn't fire too early after a long drop.
- Catches the case where MSFS pauses a minute or so after the internet blips.
- Won't press anything unless MSFS is actually the window in front.
- New icon, and the version number now shows in the app.

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
