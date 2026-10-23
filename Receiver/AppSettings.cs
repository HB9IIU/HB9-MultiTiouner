using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace QocastPlayer.Receiver
{
    // Settings that the user changes in the web page and that are kept in settings.json next to the program:
    //   - the LNB oscillator frequency (player.json still gives the starting value, 9750 MHz by default)
    //   - a frequency correction for each tuner (by USB serial number), found by the calibration on the beacon
    public static class AppSettings
    {
        // the QO-100 wideband span that the page shows
        public const long BandLowKhz = 10490500, BandHighKhz = 10499500;
        // a correction larger than this is a sign of a wrong LNB frequency, not of a small error
        public const long MaxCorrectionHz = 500000;

        private static long _lnbKhz = 9750000;
        private static volatile bool _checkUpdates = true;
        private static string _path;
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, long> CorrectionsHz = new Dictionary<string, long>();
        private static readonly Dictionary<string, DateTime> CalibratedAt = new Dictionary<string, DateTime>();

        public static long LnbKhz { get { return Interlocked.Read(ref _lnbKhz); } }

        // whether the program may ask GitHub for a newer version (on by default; Settings can switch it off)
        public static bool CheckUpdates { get { return _checkUpdates; } }

        // Reads settings.json if it exists; otherwise `defaultLnbKhz` (from player.json) is used.
        public static void Initialize(string path, long defaultLnbKhz)
        {
            lock (Sync)
            {
                _path = path;
                Interlocked.Exchange(ref _lnbKhz, defaultLnbKhz);
                CorrectionsHz.Clear(); CalibratedAt.Clear();
                _checkUpdates = true;
                try
                {
                    if (!File.Exists(path)) return;
                    JObject root = JObject.Parse(File.ReadAllText(path));
                    if (root["checkUpdates"] != null && root["checkUpdates"].Type == JTokenType.Boolean) _checkUpdates = (bool)root["checkUpdates"];
                    JToken value = root["lnbMhz"];
                    if (value != null && (value.Type == JTokenType.Float || value.Type == JTokenType.Integer))
                    {
                        long khz = (long)Math.Round((double)value * 1000.0);
                        if (CheckLnb(khz) == null) Interlocked.Exchange(ref _lnbKhz, khz);
                        else Log.Warning("settings.json: the LNB frequency {Mhz} MHz does not fit the tuner; using {Default} MHz", (double)value, defaultLnbKhz / 1000.0);
                    }
                    var calibration = root["calibration"] as JObject;
                    if (calibration != null)
                        foreach (var item in calibration)
                        {
                            var entry = item.Value as JObject;
                            JToken hz = entry == null ? null : entry["hz"];
                            if (hz == null || (hz.Type != JTokenType.Integer && hz.Type != JTokenType.Float)) continue;
                            long correction = (long)Math.Round((double)hz);
                            if (Math.Abs(correction) > MaxCorrectionHz) continue;
                            CorrectionsHz[item.Key] = correction;
                            DateTime at;
                            if (entry["at"] != null && DateTime.TryParse((string)entry["at"], null, System.Globalization.DateTimeStyles.RoundtripKind, out at)) CalibratedAt[item.Key] = at;
                        }
                }
                catch (Exception ex) { Log.Warning(ex, "Could not read settings.json; using the default settings"); }
            }
        }

        // ---- the LNB ----

        // null when the LNB frequency lets the tuner receive the whole QO-100 band, otherwise a sentence for the user.
        public static string CheckLnb(long lnbKhz)
        {
            if (lnbKhz >= BandLowKhz)
                return "The LNB frequency must be below the band (10490.5 MHz). Values like 10600 are for the upper band of a universal LNB; use the lower one, for example 9750.";
            long ifLow = BandLowKhz - lnbKhz, ifHigh = BandHighKhz - lnbKhz;
            if (ifLow < ReceiverService.MinIfKhz || ifHigh > ReceiverService.MaxIfKhz)
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "With {0:0.###} MHz the tuner would have to receive {1:0.###} to {2:0.###} MHz, but it can only receive {3} to {4} MHz. Check the value printed on your LNB.",
                    lnbKhz / 1000.0, ifLow / 1000.0, ifHigh / 1000.0, ReceiverService.MinIfKhz / 1000, ReceiverService.MaxIfKhz / 1000);
            return null;
        }

        // Validates, applies and saves. Returns null on success, otherwise the reason.
        public static string SetLnbMhz(double mhz)
        {
            if (double.IsNaN(mhz) || double.IsInfinity(mhz)) return "Enter the LNB frequency in MHz, for example 9750.";
            long khz = (long)Math.Round(mhz * 1000.0);
            string problem = CheckLnb(khz);
            if (problem != null) return problem;
            lock (Sync)
            {
                Interlocked.Exchange(ref _lnbKhz, khz);
                Save();
            }
            Log.Information("LNB frequency set to {Mhz} MHz", khz / 1000.0);
            return null;
        }

        public static void SetCheckUpdates(bool enabled)
        {
            lock (Sync) { _checkUpdates = enabled; Save(); }
            Log.Information("Checking for a newer version: {State}", enabled ? "on" : "off");
        }

        // ---- the frequency correction of each tuner ----

        // The correction in Hz for the tuner with this USB serial number (0 if it was never calibrated).
        public static long CorrectionHz(string serial)
        {
            lock (Sync)
            {
                long hz;
                return serial != null && CorrectionsHz.TryGetValue(serial, out hz) ? hz : 0;
            }
        }

        public static DateTime? CalibrationTime(string serial)
        {
            lock (Sync)
            {
                DateTime at;
                return serial != null && CalibratedAt.TryGetValue(serial, out at) ? (DateTime?)at : null;
            }
        }

        // Returns null on success, otherwise the reason.
        public static string SetCorrectionHz(string serial, long hz)
        {
            if (string.IsNullOrEmpty(serial)) return "Unknown tuner.";
            if (Math.Abs(hz) > MaxCorrectionHz)
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "The measured error ({0:0.#} kHz) is too large to be a small drift. Check the LNB frequency in Settings.", hz / 1000.0);
            lock (Sync)
            {
                CorrectionsHz[serial] = hz;
                CalibratedAt[serial] = DateTime.Now;
                Save();
            }
            Log.Information("Frequency correction of tuner {Serial}: {Khz} kHz", serial, hz / 1000.0);
            return null;
        }

        public static void ClearCorrections()
        {
            lock (Sync) { CorrectionsHz.Clear(); CalibratedAt.Clear(); Save(); }
            Log.Information("All frequency corrections were cleared");
        }

        // The frequency to send to the tuner, in kHz: the RF frequency minus the LNB frequency, plus the correction of this tuner.
        // (A carrier that the tuner sees 28 kHz too low is cured by tuning 28 kHz lower: the correction is then -28 kHz.)
        public static long IfKhz(long rfKhz, string serial)
        {
            return rfKhz - LnbKhz + (long)Math.Round(CorrectionHz(serial) / 1000.0, MidpointRounding.AwayFromZero);
        }

        // ---- file ----

        private static void Save()   // Sync must be held
        {
            try
            {
                var root = new JObject { ["lnbMhz"] = LnbKhz / 1000.0, ["checkUpdates"] = _checkUpdates };
                if (CorrectionsHz.Count > 0)
                {
                    var calibration = new JObject();
                    foreach (var item in CorrectionsHz)
                    {
                        DateTime at;
                        calibration[item.Key] = new JObject { ["hz"] = item.Value, ["at"] = CalibratedAt.TryGetValue(item.Key, out at) ? at.ToString("o") : null };
                    }
                    root["calibration"] = calibration;
                }
                string temporary = _path + ".tmp";
                File.WriteAllText(temporary, root.ToString());
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(temporary, _path);
            }
            catch (Exception ex) { Log.Warning(ex, "Could not save settings.json"); }
        }
    }
}
