using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using System;

namespace QocastPlayer.Receiver
{
    // Looks for a BATC PicoTuner on USB. It has two WinUSB interfaces ("PicoTuner i2c" and "PicoTuner TS",
    // bound with Zadig) and no USB serial number, so its two tuners get fixed names instead.
    // Same name test as PicoTunerInterface.hw_init, which does the real opening later.
    // Only one PicoTuner can be used: PicoTunerInterface keeps its USB handles in static fields.
    public static class PicoTunerDiscovery
    {
        public const string Serial1 = "PICOTUNER-1";
        public const string Serial2 = "PICOTUNER-2";

        // the two tuners of the PicoTuner; empty when there is none, or when libusb is not usable (never throws)
        public static MiniTiounerDevice[] Discover()
        {
            try
            {
                bool i2c = false, ts = false;
                foreach (UsbRegistry registry in UsbDevice.AllDevices)
                {
                    string name = registry.Name ?? "";
                    if (!name.Contains("PicoTuner"))
                        continue;
                    if (name.Contains("i2c"))
                        i2c = true;
                    else if (name.Contains("TS"))
                        ts = true;
                }

                if (!i2c || !ts)
                    return new MiniTiounerDevice[0];
                return new[]
                {
                    new MiniTiounerDevice(Serial1, "PicoTuner 1", 1) { UsbDescription = "PicoTuner i2c + TS" },
                    new MiniTiounerDevice(Serial2, "PicoTuner 2", 2) { UsbDescription = "PicoTuner i2c + TS" }
                };
            }
            catch (Exception ex)
            {
                Log.Warning("PicoTuner search skipped: " + ex.Message);
                return new MiniTiounerDevice[0];
            }
            finally
            {
                try { UsbDevice.Exit(); } catch { }     // frees libusb; PicoTunerInterface starts it again when it opens
            }
        }
    }
}
