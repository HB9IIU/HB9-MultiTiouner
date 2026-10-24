# Changelog

## Unreleased

- PicoTuner support (experimental): both tuners of a PicoTuner, and several PicoTuners at once. Tested with PicoTuner emulators only, not yet with real hardware.
- The page can show the QO-100 band plan as faint bars under the spectrum (switch "Band plan", on by default).
- The header says "N of M tuners connected", with how many MiniTiouners and PicoTuners.

## 0.1.0 (first release)

First public version of HB9 MultiTiouner.

- Several MiniTiouners at once, each numbered by its USB serial number.
- LNB frequency setting for LNBs that are not 9750 MHz (Settings window).
- Frequency calibration on the beacon, one correction per tuner.
- Tells you when a newer version is on GitHub (a bar under the header; it can be switched off in Settings).
- QO-100 wideband spectrum (BATC) with click-to-tune and draggable tuner markers.
- AutoTune, with the beacon only on a spare tuner.
- Video and sound in the browser, with an audio level meter per tuner.
- Stream out: UDP multicast per tuner, for VLC, OBS or other PCs.
- Record: every transmission except the beacon, one folder per callsign. Always off at start.
- Transport stream info card per tuner (service, PIDs, bitrates, codec, picture size, errors).
- BATC wideband chat.
- Help / About window.
- MiniTiouner devices only. PicoTuner support is in preparation.
