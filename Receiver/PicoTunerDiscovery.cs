using LibUsbDotNet;
using LibUsbDotNet.Main;
using opentuner;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace QocastPlayer.Receiver
{
    // Looks for BATC PicoTuners on USB. A PicoTuner has two WinUSB interfaces ("PicoTuner i2c" and "PicoTuner TS",
    // bound with Zadig) and no USB serial number, so each one is told apart by the part of the USB path that its two
    // interfaces share (for example "7&29532d75&0"), and its two tuners get names made from that.
    // Same name test as PicoTunerInterface.hw_init, which does the real opening later.
    public static class PicoTunerDiscovery
    {
        // ...#7&29532d75&0&0001#{guid}  ->  key "7&29532d75&0", interface "0001"
        private static readonly Regex InstancePart = new Regex(@"#(?<key>[^#]+)&(?<interface>\d{4})#\{", RegexOptions.Compiled);

        // the tuners of every PicoTuner found, in a steady order; empty when there is none, or when libusb is not usable (never throws)
        public static MiniTiounerDevice[] Discover()
        {
            try
            {
                var hasI2c = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var hasTs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (UsbRegistry registry in UsbDevice.AllDevices)
                {
                    string name = registry.Name ?? "";
                    if (!name.Contains("PicoTuner"))
                        continue;
                    Match match = InstancePart.Match(registry.SymbolicName ?? "");
                    if (!match.Success)
                    {
                        Log.Warning("PicoTuner interface with an unknown USB path: " + registry.SymbolicName);
                        continue;
                    }

                    string key = match.Groups["key"].Value;
                    if (name.Contains("i2c"))
                        hasI2c.Add(key);
                    else if (name.Contains("TS"))
                        hasTs.Add(key);
                }

                string[] keys = hasI2c.Where(hasTs.Contains).OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
                var devices = new List<MiniTiounerDevice>();
                for (int index = 0; index < keys.Length; index++)
                {
                    string label = keys.Length == 1 ? "" : ((char)('A' + index)).ToString();
                    for (int tuner = 1; tuner <= 2; tuner++)
                    {
                        // one PicoTuner: "PicoTuner 1", "PicoTuner 2"; several: "PicoTuner A1", "PicoTuner A2", "PicoTuner B1", ...
                        devices.Add(new MiniTiounerDevice("PICO-" + keys[index].ToUpperInvariant() + "-" + tuner, "PicoTuner " + label + tuner, tuner, keys[index])
                        { UsbDescription = "PicoTuner i2c + TS" });
                    }
                }
                return devices.ToArray();
            }
            catch (Exception ex)
            {
                Log.Warning("PicoTuner search skipped: " + ex.Message);
                return new MiniTiounerDevice[0];
            }
            finally
            {
                try { PicoTunerInterface.ExitIfUnused(); } catch { }     // frees libusb; PicoTunerInterface starts it again when it opens
            }
        }
    }
}