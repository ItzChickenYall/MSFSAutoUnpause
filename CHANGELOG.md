# Changelog

## 1.1.0 (unreleased)
- **Resuming actually works now.** It closes MSFS's pause menu by focusing the sim and pressing Escape, with an OCR-located click on "Resume" as a fallback. (The old SimConnect unpause command was silently ignored by MSFS 2024.)
- Fixed key presses being silently dropped on 64-bit Windows.
- Never sends input unless MSFS is confirmed to be the foreground window.
- After long outages it waits longer and re-checks that the internet has stayed up before resuming.
- Catches pauses where MSFS reacts a minute or more after a short internet blip (3-minute look-back).
- Real TCP connection check alongside DNS, so a cached DNS answer can't hide an outage.
- Ignores Windows network-port exhaustion so it can't trigger a false unpause (protects Pause-at-TOD).
- Watchdog and 5-minute heartbeat in the log, for diagnosing freezes.
- Log lines show both local time and UTC. The version number is shown in the app and the log.

## 1.0.0
- First release.
