Tuner code from Open Tuner by Tom ZR6TG and contributors
https://github.com/tomvdb/open_tuner - GNU General Public License v3 (see ..\LICENSE)

Copied from HB9IIU's modified Open Tuner (as shipped with QOCAST 0.1), which added
QocastRxInfo.cs. Changes made for the QOCAST Player:
- FTDIInterface.cs: hw_close() releases the USB ports (it was empty);
  an unused "using FlyleafLib..." line removed.

The Player's own copies of OpenTuner's NimThread.cs and QocastControlServer.cs
(changed more) are in ..\Receiver and ..\Control.

Note: QocastRxInfo.cs and the QOCAST control server (QocastControlServer.cs) were part of the
original player. They are not needed by this web app and were removed here.
