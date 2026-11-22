MSFS Auto Unpause
=================
Made by ItzChickenYall  |  Free & open source (MIT License)
Source code: https://github.com/<your-username>/MSFSAutoUnpause


WHAT IT DOES
------------
If your internet drops during a flight, Microsoft Flight Simulator 2024 pauses
and shows "Connection to network lost". On a long-haul flight while you're
asleep or away, that can mean the flight sits paused for hours.

MSFS Auto Unpause watches for this. Once your internet is back, it brings
MSFS to the front and presses Escape for you to close the pause menu, exactly
as you would by hand. If Escape doesn't work, it finds the "Resume" button on
screen and clicks it.

It ONLY acts on pauses caused by a network problem. If you pause yourself
(Escape, or the Pause-at-TOD feature), the app shows "Paused · manual" and
leaves the sim alone.


REQUIREMENTS
------------
- Microsoft Flight Simulator 2024
- Windows 10 (version 1809 or later) or Windows 11, 64-bit
- Nothing else to install - everything the app needs is in this folder.


INSTALL
-------
1. Extract the whole "MSFSAutoUnpause" folder anywhere you like
   (e.g. Documents). Do NOT put it in your MSFS Community folder - it is a
   standalone app, not a sim add-on.
2. Run MSFSAutoUnpause.exe.
   If Windows shows "Windows protected your PC", click "More info" ->
   "Run anyway". This appears because the app is not code-signed (signing
   certificates are expensive for a free hobby project). The full source
   code is public on GitHub if you'd like to check it.
3. Start MSFS and load a flight. Monitoring starts automatically.

To uninstall: untick "Start with Windows" in Settings (if you turned it on),
exit the app from the tray icon, and delete the folder. Settings and logs
are in %APPDATA%\MSFSAutoUnpause\ and can be deleted too.


STATUS MESSAGES
---------------
  Monitoring                      Flying normally
  Paused · checking…              Sim just paused, checking your internet
  Paused · manual                 You paused it - the app will not touch it
  Paused Xs · Ys left             Network pause, waiting out the minimum time
  Waiting to reconnect…           Network pause, internet still down
  Reconnected · resuming in Xs…   Internet back, letting MSFS settle first
  Unpausing…                      Closing the pause menu


SETTINGS  (gear icon, top right)
--------------------------------
- Auto-unpause after being paused for : minimum pause length (seconds)
                                         before it will resume. Default 30.
- Network watchdog                    : background internet monitoring.
                                         Leave this on.
- Start with Windows                  : launch automatically at login.

Closing the window sends the app to the system tray. Right-click the tray
icon to show it again or to exit.


GOOD TO KNOW
------------
- When it resumes, the app brings MSFS to the front and sends a real
  Escape key press or mouse click. If you're using the PC at that moment,
  MSFS will jump in front of whatever you were doing.
- If your internet dropped in the 3 minutes before the sim paused, the app
  treats the pause as network-caused (MSFS often reacts a minute or so after
  the connection actually drops). So a manual pause made right after an
  internet blip may be resumed.
- Only one copy can run at a time. If nothing opens, check the system tray.
- Logs (useful for bug reports): %APPDATA%\MSFSAutoUnpause\logs\
  Logs older than 2 days are deleted automatically.


REPORTING BUGS
--------------
Please open an issue on GitHub (or comment on Flightsim.to) and attach the
log file from the night it happened.


LICENSE
-------
MIT License - see LICENSE.txt. Provided as-is, with no warranty.
SimConnect.dll and Microsoft.FlightSimulator.SimConnect.dll are part of the
Microsoft Flight Simulator SDK and belong to Microsoft.
