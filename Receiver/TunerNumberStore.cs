using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace QocastPlayer.Receiver
{
    public static class TunerNumberStore
    {
        public static Dictionary<string, int> LoadOrAssign(IEnumerable<MiniTiounerDevice> devices, string path)
        {
            Dictionary<string, int> numbers;
            if (File.Exists(path))
            {
                numbers = JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(path, Encoding.UTF8));
                if (numbers == null)
                    throw new InvalidDataException("The tuner-number file must contain a JSON object.");
                if (numbers.Any(entry => String.IsNullOrWhiteSpace(entry.Key) || entry.Value < 1)
                    || numbers.Values.Distinct().Count() != numbers.Count)
                    throw new InvalidDataException("The tuner-number file must contain unique positive numbers and non-empty serials.");
            }
            else
            {
                numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            int next = numbers.Count == 0 ? 1 : numbers.Values.Max() + 1;
            foreach (MiniTiounerDevice device in devices.OrderBy(item => item.Serial, StringComparer.OrdinalIgnoreCase))
            {
                if (!numbers.ContainsKey(device.Serial))
                    numbers.Add(device.Serial, next++);
            }

            string json = JsonConvert.SerializeObject(numbers, Formatting.Indented);
            string temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, json + Environment.NewLine, new UTF8Encoding(false));
            if (File.Exists(path))
                File.Replace(temporaryPath, path, null);
            else
                File.Move(temporaryPath, path);
            return numbers;
        }
    }
}
