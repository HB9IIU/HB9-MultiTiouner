using QocastPlayer.Receiver;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace QocastPlayer
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool consoleAttached = ConsoleLog.Attach();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var logging = new LoggerConfiguration()
                .WriteTo.File(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HB9-MultiTiouner.log"),
                              fileSizeLimitBytes: 5 * 1024 * 1024, rollOnFileSizeLimit: true, retainedFileCountLimit: 2);
            if (consoleAttached)
                logging.WriteTo.Sink(new ConsoleLog());
            Log.Logger = logging.CreateLogger();
            Log.Information("HB9-MultiTiouner {Version} (built {Built}) starting", QocastPlayer.AppVersion.Number, QocastPlayer.AppVersion.Built);

            using (var single = new System.Threading.Mutex(true, "HB9-MultiTiouner", out bool first))
            {
                if (!first)
                {
                    Log.Information("Already running, exiting");
                    return 2;
                }

                try
                {
                    return Run(args);
                }
                catch (Exception ex)
                {
                    Log.Fatal(ex, "Player stopped");
                    MessageBox.Show(ex.Message, "HB9-MultiTiouner", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                finally
                {
                    Log.CloseAndFlush();
                }
            }
        }

        private static int Run(string[] args)
        {
            string path = PlayerConfig.DefaultPath;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--config")
                    path = Path.GetFullPath(args[i + 1]);
            }

            PlayerConfig config;
            try
            {
                config = PlayerConfig.Load(path);
            }
            catch (ConfigException ex)
            {
                Log.Error("Config: " + ex.Message);
                MessageBox.Show(ex.Message, "HB9-MultiTiouner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            // the LNB frequency: settings.json (changed in the web page) wins over player.json
            AppSettings.Initialize(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"), config.lnb_offset_khz);

            var found = new List<MiniTiounerDevice>();
            Exception ftdiProblem = null;
            try
            {
                found.AddRange(MiniTiounerDiscovery.Discover());
            }
            catch (Exception ex)
            {
                // e.g. no FTDI driver installed: fine if a PicoTuner is there, reported below if nothing is
                Log.Error(ex, "MiniTiouner discovery failed");
                ftdiProblem = ex;
            }

            MiniTiounerDevice pico = PicoTunerDiscovery.Discover();
            if (pico != null)
                found.Add(pico);
            MiniTiounerDevice[] devices = found.ToArray();

            if (devices.Length == 0 && ftdiProblem != null)
            {
                MessageBox.Show(ftdiProblem.Message, "HB9-MultiTiouner - tuner discovery",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            if (devices.Length == 0)
            {
                const string message = "No usable MiniTiouner or PicoTuner was found. Connect a tuner and check its driver.";
                Log.Warning(message);
                if (args.Contains("--list-devices"))
                    return 2;
                MessageBox.Show("No usable MiniTiouner or PicoTuner was found." + Environment.NewLine + Environment.NewLine + "1. Connect a MiniTiouner or a PicoTuner by USB." + Environment.NewLine + "2. MiniTiouner: install the FTDI D2XX driver. PicoTuner: bind its two USB interfaces to the WinUSB driver (Zadig)." + Environment.NewLine + "3. Close any other program that uses the tuner (OpenTuner, the MiniTiouner app).", "HB9-MultiTiouner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            Log.Information("Found {Count} tuner devices", devices.Length);
            foreach (MiniTiounerDevice device in devices)
                Log.Information("Tuner: {Name}, serial {Serial}", device.Name, device.Serial);
            if (args.Contains("--list-devices"))
                return 0;
            if (args.Contains("--probe-open"))
                return ProbeOpen(config, devices);
            Dictionary<string, int> tunerNumbers;
            try
            {
                string numberPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tuner-numbers.json");
                tunerNumbers = TunerNumberStore.LoadOrAssign(devices, numberPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not load tuner numbering");
                MessageBox.Show(ex.Message, "HB9-MultiTiouner - tuner numbering",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            int result;
            using (var app = new BrowserApp(config, devices, tunerNumbers))
                result = app.Run();

            Log.Information("Bye");
            return result;
        }

        private static int ProbeOpen(PlayerConfig config, MiniTiounerDevice[] devices)
        {
            var receivers = new List<ReceiverService>();
            try
            {
                foreach (MiniTiounerDevice device in devices)
                {
                    var receiver = new ReceiverService(config, device.Serial, device.IsPicoTuner);
                    receivers.Add(receiver);
                    receiver.Start();
                }

                DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                while (receivers.Any(receiver => !receiver.Connected) && DateTime.UtcNow < deadline)
                    Thread.Sleep(100);

                bool allConnected = true;
                for (int i = 0; i < receivers.Count; i++)
                {
                    ReceiverService receiver = receivers[i];
                    Log.Information("Open test {Serial}: connected={Connected}, state={State}",
                        devices[i].Serial, receiver.Connected, receiver.State);
                    allConnected &= receiver.Connected;
                }
                return allConnected ? 0 : 2;
            }
            finally
            {
                foreach (ReceiverService receiver in receivers)
                    receiver.Dispose();
            }
        }
    }
}
