using FTD2XX_NET;
using opentuner;
using System;
using System.Collections.Generic;
using System.Linq;

namespace QocastPlayer.Receiver
{
    public sealed class MiniTiounerDevice
    {
        // picoTuner: 0 for a MiniTiouner; 1 or 2 for the first or second tuner of a PicoTuner
        public MiniTiounerDevice(string serial, string name, int picoTuner = 0)
        {
            Serial = serial;
            Name = name;
            PicoTuner = picoTuner;
        }

        public string Serial { get; }
        public string Name { get; }
        public int PicoTuner { get; }
        public bool IsPicoTuner { get { return PicoTuner > 0; } }
        public string UsbDescription { get; set; }
        public string DriverVersion { get; set; }
    }

    public static class MiniTiounerDiscovery
    {
        public static IReadOnlyList<MiniTiounerDevice> Discover()
        {
            var ports = new Dictionary<string, DevicePorts>(StringComparer.OrdinalIgnoreCase);
            var probe = new FTDI();
            uint count = 0;
            FTDI.FT_STATUS status = probe.GetNumberOfDevices(ref count);
            if (status != FTDI.FT_STATUS.FT_OK)
                throw new InvalidOperationException("Could not enumerate FTDI devices: " + status);

            for (uint index = 0; index < count; index++)
            {
                bool opened = false;
                try
                {
                    status = probe.OpenByIndex(index);
                    if (status != FTDI.FT_STATUS.FT_OK)
                        throw new InvalidOperationException("Could not inspect FTDI device " + index + ": " + status);
                    opened = true;

                    FTDI.FT_DEVICE type = new FTDI.FT_DEVICE();
                    status = probe.GetDeviceType(ref type);
                    if (status != FTDI.FT_STATUS.FT_OK)
                        throw new InvalidOperationException("Could not read the type of FTDI device " + index + ": " + status);
                    if (type != FTDI.FT_DEVICE.FT_DEVICE_2232H)
                        continue;

                    string serial;
                    string description;
                    status = probe.GetSerialNumber(out serial);
                    if (status != FTDI.FT_STATUS.FT_OK)
                        throw new InvalidOperationException("Could not read the serial number of FTDI device " + index + ": " + status);
                    status = probe.GetDescription(out description);
                    if (status != FTDI.FT_STATUS.FT_OK)
                        throw new InvalidOperationException("Could not read the description of FTDI device " + index + ": " + status);

                    string key = FTDIInterface.NormalizeDeviceSerial(serial);
                    if (String.IsNullOrWhiteSpace(key) || !IsMiniTiounerDescription(description))
                        continue;

                    DevicePorts device;
                    if (!ports.TryGetValue(key, out device))
                    {
                        device = new DevicePorts(key);
                        ports.Add(key, device);
                    }

                    if (IsTunerA(description))
                    {
                        device.HasPortA = true;
                        device.Name = GetDeviceName(description);
                        device.Description = description;
                        uint driverVersion = 0;
                        if (probe.GetDriverVersion(ref driverVersion) == FTDI.FT_STATUS.FT_OK)
                            device.DriverVersion = ((driverVersion >> 16) & 255) + "." + ((driverVersion >> 8) & 255) + "." + (driverVersion & 255);
                    }
                    else if (IsTunerTsPortB(description))
                    {
                        device.HasPortB = true;
                    }
                }
                finally
                {
                    if (opened)
                        probe.Close();
                }
            }

            return ports.Values
                .Where(device => device.HasPortA && device.HasPortB)
                .OrderBy(device => device.Serial, StringComparer.OrdinalIgnoreCase)
                .Select(device => new MiniTiounerDevice(device.Serial, device.Name) { UsbDescription = device.Description, DriverVersion = device.DriverVersion })
                .ToArray();
        }

        private static bool IsMiniTiounerDescription(string description)
        {
            return description.IndexOf("MiniTiouner", StringComparison.OrdinalIgnoreCase) >= 0
                || description.IndexOf("NIM tuner", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsTunerA(string description)
        {
            return description.IndexOf("NIM tuner A", StringComparison.OrdinalIgnoreCase) >= 0
                || description.EndsWith(" A", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTunerTsPortB(string description)
        {
            return description.IndexOf("NIM tuner B", StringComparison.OrdinalIgnoreCase) >= 0
                || description.IndexOf("MiniTiouner_Pro_TS2 B", StringComparison.OrdinalIgnoreCase) >= 0
                || description.IndexOf("MiniTiouner B", StringComparison.OrdinalIgnoreCase) >= 0
                || description.IndexOf("Minitiouner S B", StringComparison.OrdinalIgnoreCase) >= 0
                || description.IndexOf("MiniTiouner-Express B", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetDeviceName(string description)
        {
            if (description.IndexOf("MiniTiouner_Pro_TS2", StringComparison.OrdinalIgnoreCase) >= 0)
                return "MiniTiouner Pro";
            if (description.IndexOf("MiniTiouner-Express", StringComparison.OrdinalIgnoreCase) >= 0)
                return "MiniTiouner Express";
            if (description.IndexOf("NIM tuner", StringComparison.OrdinalIgnoreCase) >= 0)
                return "MiniTiouner";
            return description.Substring(0, description.Length - 2);
        }

        private sealed class DevicePorts
        {
            public DevicePorts(string serial)
            {
                Serial = serial;
                Name = "MiniTiouner";
            }

            public string Serial { get; }
            public string Name { get; set; }
            public string Description { get; set; }
            public string DriverVersion { get; set; }
            public bool HasPortA { get; set; }
            public bool HasPortB { get; set; }
        }
    }
}
