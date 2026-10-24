# Third-party parts

HB9 MultiTiouner is free software under the GNU General Public License v3.0 (see `LICENSE`). It is given away free of charge and is not for sale. It contains or uses the following parts made by others.

## Tuner code

- **Open Tuner** by Tom ZR6TG and contributors, https://github.com/tomvdb/open_tuner, GPL-3.0.
  The MiniTiouner and PicoTuner drivers in the `OpenTuner` folder and `Receiver\NimThread.cs` are copied from it, through HB9IIU's modified copy as shipped with the QOCAST Player. See `OpenTuner\README.txt`.

## Programs in the release zip (folder `tools`)

These are separate programs. They are not part of the source code in this repository.

- **FFmpeg**, https://ffmpeg.org, built as an LGPL build (the licence text is `tools\ffmpeg\COPYING.LGPLv2.1.txt`).
  Version `N-127094-gf68e1afc1`, built with GCC 16.2.0 by the MSYS2 project.
  The source code of exactly this version is the FFmpeg git commit `f68e1afc1` at https://git.ffmpeg.org/ffmpeg.git .
  On request I will also send the source code and the build settings free of charge.
- **MediaMTX** v1.21.2, https://github.com/bluenviron/mediamtx, MIT licence (`tools\mediamtx\LICENSE.txt`).

## Native library (file `libusb-1.0.dll`, for the PicoTuner)

- **libusb** 1.0.27 (x64), https://libusb.info, GNU LGPL v2.1 or later (the same licence text as `tools\ffmpeg\COPYING.LGPLv2.1.txt`). Copied unchanged from the Open Tuner release. The source code is at https://github.com/libusb/libusb .

## Web page

- **Bootstrap** CSS, MIT licence (`Web\BOOTSTRAP-LICENSE.txt`).

## Libraries

- **Newtonsoft.Json**, MIT.
- **Serilog** and **Serilog.Sinks.File**, Apache-2.0.
- **LibUsbDotNet**, see https://github.com/LibUsbDotNet/LibUsbDotNet/blob/master/LICENSE .
- **FTD2XX_NET** (the .NET wrapper of the FTDI D2XX driver), Copyright FTDI.

## Services

- The wideband spectrum and the chat come from **BATC** (https://eshail.batc.org.uk). The program only reads them (and writes to the chat when you do).
