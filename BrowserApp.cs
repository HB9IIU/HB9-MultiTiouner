using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QocastPlayer.Receiver;
using QocastPlayer.Video;
using Serilog;
using opentuner;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace QocastPlayer
{
    internal sealed class BrowserApp : IDisposable
    {
        private const int Port = 47322;
        private readonly string _sessionId = Guid.NewGuid().ToString("N");
        private static readonly double[] StartFrequencies = { 10491.5, 10492.75, 10493.5, 10494.0 };
        private static readonly uint[] StartSymbolRates = { 1500, 500, 333, 333 };

        private readonly ReceiverNode[] _receivers;
        private readonly HttpListener _listener = new HttpListener();
        private readonly BrowserVideoRelay _videoRelay;
        private readonly object _stateSync = new object();
        private volatile bool _stopping;
        private volatile bool _started;
        private TrayController _tray;
        private BatcMonitor _monitor;
        private readonly AutoTunePolicy _policy = new AutoTunePolicy();
        private readonly QocastPlayer.Control.BatcChat _chat = new QocastPlayer.Control.BatcChat();
        private readonly Dictionary<int, QocastPlayer.Control.TsAnalyzer> _tsAnalyzers = new Dictionary<int, QocastPlayer.Control.TsAnalyzer>();
        private readonly QocastPlayer.Control.UpdateChecker _updates = new QocastPlayer.Control.UpdateChecker(AppVersion.Number);
        private readonly QocastPlayer.Control.Calibrator _calibrator = new QocastPlayer.Control.Calibrator();
        private volatile bool _calibrating;
        private readonly QocastPlayer.Control.Recorder _recorder = new QocastPlayer.Control.Recorder(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recordings"), Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recorder.json"));
        private readonly QocastPlayer.Control.StreamOut _streamOut = new QocastPlayer.Control.StreamOut(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "streamout.json"));
        private readonly ManualResetEvent _controlStop = new ManualResetEvent(false);
        private Thread _controlThread;
        private bool _autoTune;
        private long _scanVersion;
        private string _scanStatus = "Waiting for reception";
        private readonly string _autoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "autotune.json");

        public BrowserApp(PlayerConfig config, MiniTiounerDevice[] devices, Dictionary<string, int> tunerNumbers)
        {
            _receivers = devices
                .OrderBy(device => tunerNumbers[device.Serial])
                .Select(device =>
                {
                    int number = tunerNumbers[device.Serial];
                    int tuneIndex = Math.Min(number - 1, StartFrequencies.Length - 1);
                    return new ReceiverNode(
                        number,
                        device,
                        new ReceiverService(config, device.Serial, device.IsPicoTuner),
                        StartFrequencies[tuneIndex],
                        StartSymbolRates[tuneIndex]);
                })
                .ToArray();
            _videoRelay = new BrowserVideoRelay(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime"),
                _receivers.Select(receiver => receiver.Number).ToArray());
            _monitor = new BatcMonitor();
            foreach (ReceiverNode node in _receivers)
            {
                int tunerNumber = node.Number;
                var analyzer = new QocastPlayer.Control.TsAnalyzer();
                _tsAnalyzers[tunerNumber] = analyzer;
                node.Service.TsTap = (data, count) => { _streamOut.Feed(tunerNumber, data, count); analyzer.Feed(data, count); _recorder.Feed(tunerNumber, data, count); };
            }
            try { if (File.Exists(_autoPath)) _autoTune = JObject.Parse(File.ReadAllText(_autoPath)).Value<bool>("enabled"); }
            catch (Exception ex) { Log.Warning(ex, "Could not read AutoTune preference; using off"); }
        }

        public int Run()
        {
            string prefix = "http://127.0.0.1:" + Port + "/";
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            _updates.Start();   // asks GitHub once a day whether a newer version exists (Settings can switch it off)
            string url = prefix;
            _tray = new TrayController(url, Stop);
            _controlThread = new Thread(ControlLoop) { IsBackground = true, Name = "AutoTune controller" };
            _controlThread.Start();

            Log.Information("Local web app listening at {Url}", url);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            while (!_stopping)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch (HttpListenerException) when (_stopping)
                {
                    break;
                }

                try
                {
                    if (!ThreadPool.QueueUserWorkItem(
                        state => HandleRequestSafely((HttpListenerContext)state), context))
                    {
                        Log.Error("Could not queue web request for {Path}", context.Request.Url.AbsolutePath);
                        context.Response.Abort();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Could not queue web request");
                    context.Response.Abort();
                }
            }

            return 0;
        }

        private void HandleRequestSafely(HttpListenerContext context)
        {
            try
            {
                HandleRequest(context);
            }
            catch (Exception ex) when (ex is InvalidDataException
                || ex is JsonException
                || ex is OverflowException
                || ex is FormatException)
            {
                TryWriteError(context.Response, 400, ex.Message);
            }
            catch (HttpListenerException ex)
            {
                Log.Debug(ex, "Web client disconnected");
            }
            catch (IOException ex)
            {
                Log.Debug(ex, "Web response could not be written");
            }
            catch (ObjectDisposedException ex)
            {
                Log.Debug(ex, "Web response was already closed");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Web request failed for {Path}", context.Request.Url.AbsolutePath);
                TryWriteError(context.Response, 500, ex.Message);
            }
        }

        private static void TryWriteError(HttpListenerResponse response, int statusCode, string message)
        {
            try { WriteJson(response, statusCode, new { error = message }); }
            catch (Exception ex) when (ex is HttpListenerException || ex is IOException
                || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                Log.Debug(ex, "Error response could not be sent; response already closed or started");
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            string path = context.Request.Url.AbsolutePath;
            if (context.Request.HttpMethod == "GET" && path.StartsWith("/stream/", StringComparison.Ordinal))
            {
                StreamTransport(context, path);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/")
            {
                WriteFile(context.Response, "index.html", "text/html; charset=utf-8");
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/startup-scan.js")
            {
                WriteFile(context.Response, "startup-scan.js", "application/javascript; charset=utf-8");
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/magic-eye.js")
            {
                WriteFile(context.Response, "magic-eye.js", "application/javascript; charset=utf-8");
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/bootstrap.min.css")
            {
                WriteFile(context.Response, "bootstrap.min.css", "text/css; charset=utf-8");
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/api/state")
            {
                lock (_stateSync)
                {
                    if (_started)
                        _videoRelay.EnsurePublishersRunning(_receivers.Where(r => !r.Slot.Parked).Select(r => r.Number));
                    WriteJson(context.Response, 200, new
                    {
                        sessionId = _sessionId,
                        version = AppVersion.Number,
                        recorder = _recorder.Snapshot(),
                        update = _updates.Snapshot(),
                        built = AppVersion.Built,
                        started = _started,
                        autoTune = _autoTune,
                        scanStatus = _scanStatus,
                        webRtcPort = BrowserVideoRelay.WebRtcPort,
                        receivers = _receivers.Select(receiver => receiver.GetState(_videoRelay.GetDiagnostics(receiver.Number)))
                    });
                }
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/api/start")
            {
                JObject request;
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                    request = JObject.Parse(reader.ReadToEnd());
                StartReceivers();
                WriteJson(context.Response, 200, new { started = true });
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/api/autotune")
            {
                JObject request;
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding)) request = JObject.Parse(reader.ReadToEnd());
                if (request["enabled"] == null || request["enabled"].Type != JTokenType.Boolean) throw new InvalidDataException("AutoTune enabled must be true or false");
                lock (_stateSync)
                {
                    bool enabled = request.Value<bool>("enabled");
                    string temporary = _autoPath + ".tmp";
                    File.WriteAllText(temporary, new JObject { ["enabled"] = enabled }.ToString());
                    if (File.Exists(_autoPath)) File.Replace(temporary, _autoPath, null); else File.Move(temporary, _autoPath);
                    _autoTune = enabled;
                    WriteJson(context.Response, 200, new { autoTune = _autoTune });
                }
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/api/tune")
            {
                Tune(context);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/api/shutdown")
            {
                WriteJson(context.Response, 200, new { stopping = true });
                Stop();
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/api/settings")
            {
                WriteJson(context.Response, 200, SettingsSnapshot());
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/api/settings")
            {
                JObject request;
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                    request = JObject.Parse(reader.ReadToEnd());
                JToken updatesSwitch = request["checkUpdates"];
                if (updatesSwitch != null)
                {
                    if (updatesSwitch.Type != JTokenType.Boolean) throw new InvalidDataException("checkUpdates must be true or false");
                    AppSettings.SetCheckUpdates((bool)updatesSwitch);
                    if ((bool)updatesSwitch) _updates.CheckSoon();
                    WriteJson(context.Response, 200, SettingsSnapshot());
                    return;
                }
                JToken lnb = request["lnbMhz"];
                if (lnb == null || (lnb.Type != JTokenType.Float && lnb.Type != JTokenType.Integer))
                    throw new InvalidDataException("Enter the LNB frequency in MHz, for example 9750.");
                if (_calibrating) throw new InvalidDataException("The tuners are being calibrated. Please wait until it has finished.");
                string problem = AppSettings.SetLnbMhz((double)lnb);
                if (problem != null) throw new InvalidDataException(problem);
                RetuneAll();   // every tuner that is tuned keeps its RF frequency; only the frequency sent to the tuner changes
                WriteJson(context.Response, 200, SettingsSnapshot());
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/api/calibration")
            {
                WriteJson(context.Response, 200, CalibrationSnapshot());
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/api/calibration/start")
            {
                StartCalibration();
                WriteJson(context.Response, 200, CalibrationSnapshot());
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/api/calibration/cancel")
            {
                _calibrator.Cancel();
                WriteJson(context.Response, 200, CalibrationSnapshot());
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/api/calibration/reset")
            {
                if (_calibrating) throw new InvalidDataException("The tuners are being calibrated. Please wait until it has finished.");
                AppSettings.ClearCorrections();
                RetuneAll();
                WriteJson(context.Response, 200, CalibrationSnapshot());
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/api/recorder")
            {
                JObject request;
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                    request = JObject.Parse(reader.ReadToEnd());
                if (request["enabled"] == null || request["enabled"].Type != JTokenType.Boolean) throw new InvalidDataException("Recorder enabled must be true or false");
                _recorder.SetEnabled((bool)request["enabled"]);
                WriteJson(context.Response, 200, _recorder.Snapshot());
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/api/ts")
            {
                int tunerNumber;
                QocastPlayer.Control.TsAnalyzer analyzer;
                if (!Int32.TryParse(context.Request.QueryString["number"], out tunerNumber) || !_tsAnalyzers.TryGetValue(tunerNumber, out analyzer))
                    WriteJson(context.Response, 404, new { error = "Unknown tuner number" });
                else
                    WriteJson(context.Response, 200, analyzer.Snapshot());
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/api/streamout")
            {
                WriteJson(context.Response, 200, _streamOut.Snapshot(_receivers.Select(receiver => receiver.Number)));
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/api/streamout")
            {
                JObject request;
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                    request = JObject.Parse(reader.ReadToEnd());
                _streamOut.Update(request);
                WriteJson(context.Response, 200, _streamOut.Snapshot(_receivers.Select(receiver => receiver.Number)));
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/api/chat")
            {
                _chat.Start();   // connects at the first request of the chat panel
                long since, generation;
                if (!Int64.TryParse(context.Request.QueryString["since"], out since)) since = 0;
                if (!Int64.TryParse(context.Request.QueryString["generation"], out generation)) generation = -1;
                WriteJson(context.Response, 200, _chat.Snapshot(since, generation));
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/api/chat/send")
            {
                JObject request;
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                    request = JObject.Parse(reader.ReadToEnd());
                string chatError;
                if (!_chat.Send((string)request["callsign"], (string)request["name"], (string)request["text"], out chatError))
                    WriteJson(context.Response, 409, new { error = chatError });
                else
                    WriteJson(context.Response, 202, new { accepted = true });
                return;
            }

            WriteJson(context.Response, 404, new { error = "Not found" });
        }

        private static object SettingsSnapshot()
        {
            long lnb = AppSettings.LnbKhz;
            return new
            {
                lnbMhz = lnb / 1000.0,
                checkUpdates = AppSettings.CheckUpdates,
                bandLowMhz = AppSettings.BandLowKhz / 1000.0,
                bandHighMhz = AppSettings.BandHighKhz / 1000.0,
                tunerMinMhz = ReceiverService.MinIfKhz / 1000.0,
                tunerMaxMhz = ReceiverService.MaxIfKhz / 1000.0
            };
        }

        // Every tuner that is tuned keeps its RF frequency; what is sent to the tuner is worked out again (LNB frequency, corrections).
        private void RetuneAll()
        {
            lock (_stateSync)
            {
                foreach (var receiver in _receivers)
                {
                    if (receiver.Slot.Parked) continue;
                    try { Assign(receiver, receiver.Slot.Frequency.Value, receiver.Slot.Rate); }
                    catch (Exception ex) { Log.Warning(ex, "Could not retune tuner {Number}", receiver.Number); }
                }
            }
        }

        private object CalibrationSnapshot()
        {
            return new
            {
                started = _started,
                calibration = _calibrator.Snapshot(),
                saved = _receivers.Select(r => new
                {
                    number = r.Number,
                    correctionKhz = Math.Round(AppSettings.CorrectionHz(r.Device.Serial) / 1000.0, 2),
                    at = AppSettings.CalibrationTime(r.Device.Serial)?.ToString("s")
                }).ToArray()
            };
        }

        // All connected tuners are tuned to the beacon, away and back, and their frequency error is measured (see Calibrator).
        // The tuners return to what they were doing when it is finished.
        private void StartCalibration()
        {
            lock (_stateSync)
            {
                if (!_started) throw new InvalidDataException("Start receiving first.");
                if (_calibrator.Running || _calibrating) throw new InvalidDataException("A calibration is already running.");
                var before = _receivers.Select(r => new { Node = r, Frequency = r.Slot.Parked ? (double?)null : r.Slot.Frequency, Rate = r.Slot.Rate }).ToList();
                var targets = _receivers.Where(r => r.Service.Connected).Select(node => new QocastPlayer.Control.CalibrationTarget
                {
                    Number = node.Number,
                    Serial = node.Device.Serial,
                    Name = node.Device.Name,
                    Tune = (mhz, rate) => { lock (_stateSync) Assign(node, mhz, rate); },
                    IsLocked = () => !node.Slot.Parked && node.Service.State == ReceiverState.Locked,
                    OffsetHz = () =>
                    {
                        TunerStatus status = node.Service.LastStatus;
                        return node.Service.State == ReceiverState.Locked && status != null ? (long?)status.T1P2_frequency_carrier_offset : null;
                    }
                }).ToList();
                if (targets.Count == 0) throw new InvalidDataException("No tuner is connected.");
                _calibrating = true;
                if (!_calibrator.Start(targets, () =>
                {
                    lock (_stateSync)
                    {
                        foreach (var item in before)
                        {
                            try { if (item.Frequency.HasValue) Assign(item.Node, item.Frequency.Value, item.Rate); else Park(item.Node); }
                            catch (Exception ex) { Log.Warning(ex, "Could not restore tuner {Number} after the calibration", item.Node.Number); }
                        }
                        _calibrating = false;
                    }
                })) { _calibrating = false; throw new InvalidDataException("A calibration is already running."); }
            }
        }

        private void StartReceivers()
        {
            // Wait briefly for the backend feed, never hold the state lock during network waiting.
            ushort[] samples; long version;
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (!_monitor.Snapshot(out samples, out version) && DateTime.UtcNow < deadline && !_stopping) Thread.Sleep(100);
            if (!_monitor.Snapshot(out samples, out version)) samples = null;
            bool beacon;
            // Stations first, left to right; the beacon only takes a tuner that is left over.
            var stations = BatcMonitor.Detect(samples, out beacon).OrderBy(c => c.FrequencyMhz)
                .Take(_receivers.Length).ToArray();
            lock (_stateSync)
            {
                if (_started || _stopping) return;
                int next = 0;
                // Tuner 1 shows the beacon whenever a tuner is spare; with as many stations as tuners, all go to stations.
                bool beaconFirst = stations.Length < _receivers.Length, beaconAssigned = false;
                foreach (ReceiverNode receiver in _receivers)
                {
                    receiver.Service.Start();
                    if (beaconFirst && !beaconAssigned) { Assign(receiver, 10491.5, 1500); beaconAssigned = true; }
                    else if (next < stations.Length)
                    { var station = stations[next++]; Assign(receiver, station.FrequencyMhz, station.SymbolRateKsps); }
                    else Park(receiver);
                }
                _started = true;
                Log.Information("Started {Count} receivers; AutoTune {AutoTune}", _receivers.Length, _autoTune);
            }
        }

        private void Assign(ReceiverNode receiver, double frequency, uint rate)
        {
            string error = receiver.Service.CheckTune((long)Math.Round(frequency * 1000), rate);
            if (error != null) throw new InvalidDataException(error);
            Interlocked.Increment(ref receiver.StreamGeneration);
            receiver.Slot.Assign(frequency, rate, DateTime.UtcNow);
            receiver.Service.Tune((long)Math.Round(frequency * 1000), rate);
            _videoRelay.RestartPublisher(receiver.Number);
            Log.Information("Assigned tuner {Number}: {Frequency} MHz / {Rate} kS/s", receiver.Number, frequency, rate);
        }

        private void Park(ReceiverNode receiver)
        {
            Interlocked.Increment(ref receiver.StreamGeneration);
            receiver.Slot.Park();
            receiver.Service.Park();
            _videoRelay.SuspendPublisher(receiver.Number);
            Log.Information("Parked tuner {Number}", receiver.Number);
        }

        private void ControlLoop()
        {
            while (!_controlStop.WaitOne(1000))
            {
                try
                {
                    ushort[] samples; long version;
                    bool fresh = _monitor.Snapshot(out samples, out version), beacon = false;
                    var carriers = fresh ? BatcMonitor.Detect(samples, out beacon) : new List<ScanCarrier>();
                    lock (_stateSync)
                    {
                        _scanStatus = !fresh ? "BATC feed unavailable · automatic assignment paused" : _autoTune ? "AutoTune · monitoring band" : "Manual tuning";
                        if (!_started || _stopping) continue;
                        if (_calibrating) { _scanStatus = "Calibrating the tuners on the beacon"; continue; }
                        foreach (var receiver in _receivers)
                        { receiver.Slot.Connected = receiver.Service.Connected; receiver.Slot.Locked = receiver.Service.State == ReceiverState.Locked; }
                        foreach (var node in _receivers)   // the recorder: everything a tuner is locked on, the beacon excepted
                            _recorder.Tick(node.Number, !node.Slot.Parked && node.Slot.Locked && !node.Slot.OnBeacon, _tsAnalyzers[node.Number].ServiceName());
                        var actions = _policy.Tick(_receivers.Select(r => r.Slot).ToList(), carriers, fresh, beacon,
                            version != _scanVersion, _autoTune, DateTime.UtcNow);
                        _scanVersion = version;
                        foreach (var action in actions)
                        {
                            var receiver = _receivers.First(r => r.Number == action.Slot.Number);
                            if (action.Carrier == null) Park(receiver);
                            else Assign(receiver, action.Carrier.FrequencyMhz, action.Carrier.SymbolRateKsps);
                        }
                    }
                }
                catch (Exception ex) { Log.Error(ex, "AutoTune control step failed"); }
            }
        }

        private void Tune(HttpListenerContext context)
        {
            JObject request;
            using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                request = JObject.Parse(reader.ReadToEnd());

            int number = RequiredInt(request, "number");
            double frequencyMhz = RequiredDouble(request, "frequencyMhz");
            int requestedSymbolRate = RequiredInt(request, "symbolRateKsps");
            ReceiverNode receiver = _receivers.FirstOrDefault(item => item.Number == number);
            if (receiver == null)
            {
                WriteJson(context.Response, 404, new { error = "Unknown tuner number" });
                return;
            }

            if (Double.IsNaN(frequencyMhz) || Double.IsInfinity(frequencyMhz))
            {
                WriteJson(context.Response, 400, new { error = "Frequency must be a finite number" });
                return;
            }

            if (requestedSymbolRate < 1 || requestedSymbolRate > 10000)
            {
                WriteJson(context.Response, 400, new { error = "Symbol rate must be from 1 to 10000 kS/s" });
                return;
            }

            if (frequencyMhz < 100 || frequencyMhz > 100000)
            {
                WriteJson(context.Response, 400, new { error = "Frequency is outside the supported range" });
                return;
            }

            long frequencyKhz = Decimal.ToInt64((decimal)frequencyMhz * 1000m);
            uint symbolRateKsps = (uint)requestedSymbolRate;
            string validation = receiver.Service.CheckTune(frequencyKhz, symbolRateKsps);
            if (validation != null)
            {
                WriteJson(context.Response, 400, new { error = validation });
                return;
            }

            lock (_stateSync)
            {
                if (!_started) throw new InvalidDataException("Start receiving before tuning");
                if (_calibrating) throw new InvalidDataException("The tuners are being calibrated. Please wait until it has finished.");
                if (_receivers.Any(r => r.Number != number && !r.Slot.Parked && AutoTunePolicy.Same(r.Slot.Frequency.Value, frequencyMhz, r.Slot.Rate, symbolRateKsps)))
                    throw new InvalidDataException("This transmission is already assigned to another tuner");
                if (!receiver.Slot.Parked) _policy.Cooldown(receiver.Slot.Frequency.Value, DateTime.UtcNow);
                Assign(receiver, frequencyMhz, symbolRateKsps);
            }
            WriteJson(context.Response, 200, new { tuned = true });
        }

        private void StreamTransport(HttpListenerContext context, string path)
        {
            int number;
            if (!Int32.TryParse(path.Substring("/stream/".Length), out number))
            {
                WriteJson(context.Response, 400, new { error = "Invalid tuner number" });
                return;
            }

            ReceiverNode receiver = _receivers.FirstOrDefault(item => item.Number == number);
            if (receiver == null)
            {
                WriteJson(context.Response, 404, new { error = "Unknown tuner number" });
                return;
            }

            if (receiver.Slot.Parked)
            {
                WriteJson(context.Response, 409, new { error = "Tuner is parked" });
                return;
            }
            int generation = receiver.StreamGeneration;
            HttpListenerResponse response = context.Response;
            response.StatusCode = 200;
            response.ContentType = "video/mp2t";
            response.SendChunked = true;
            response.KeepAlive = true;
            response.Headers["Cache-Control"] = "no-store";

            try
            {
                response.OutputStream.Flush();
                while (!_stopping && generation == receiver.StreamGeneration)
                {
                    byte[] transportData = receiver.Service.Stream.TryDequeueBytes(16 * 1024);
                    if (transportData.Length == 0)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    response.OutputStream.Write(transportData, 0, transportData.Length);
                    response.OutputStream.Flush();
                }
            }
            catch (HttpListenerException ex)
            {
                Log.Debug(ex, "Transport stream client for tuner {Number} disconnected", number);
            }
            catch (IOException ex)
            {
                Log.Debug(ex, "Transport stream write for tuner {Number} stopped", number);
            }
            finally
            {
                response.Close();
            }
        }

        private void WriteFile(HttpListenerResponse response, string fileName, string contentType)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Web", fileName);
            byte[] contents = File.ReadAllBytes(path);
            response.ContentType = contentType;
            response.StatusCode = 200;
            response.ContentLength64 = contents.Length;
            response.OutputStream.Write(contents, 0, contents.Length);
            response.Close();
        }

        private static void WriteJson(HttpListenerResponse response, int statusCode, object value)
        {
            byte[] contents = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = contents.Length;
            response.OutputStream.Write(contents, 0, contents.Length);
            response.Close();
        }

        private static int RequiredInt(JObject value, string name)
        {
            JToken token = value[name];
            if (token == null || token.Type != JTokenType.Integer)
                throw new InvalidDataException(name + " must be an integer");
            return (int)token;
        }

        private static double RequiredDouble(JObject value, string name)
        {
            JToken token = value[name];
            if (token == null || token.Type != JTokenType.Float && token.Type != JTokenType.Integer)
                throw new InvalidDataException(name + " must be a number");
            return (double)token;
        }

        private void Stop()
        {
            _stopping = true;
            _controlStop.Set();
            if (_listener.IsListening)
                _listener.Stop();
        }

        public void Dispose()
        {
            _stopping = true;
            _controlStop.Set();
            if (_listener.IsListening)
                _listener.Stop();
            _listener.Close();
            if (_controlThread != null) _controlThread.Join(5000);
            _calibrator.Cancel();
            _updates.Dispose();
            _recorder.Dispose();
            _chat.Dispose();
            _streamOut.Dispose();
            _monitor?.Dispose();
            _videoRelay.Dispose();
            foreach (ReceiverNode receiver in _receivers)
                receiver.Service.Dispose();
            _tray?.Dispose();
        }

        private sealed class ReceiverNode
        {
            public ReceiverNode(int number, MiniTiounerDevice device, ReceiverService service,
                double initialFrequencyMhz, uint initialSymbolRateKsps)
            {
                Number = number;
                Slot = new TunerSlot { Number = number };
                Device = device;
                Service = service;
                InitialFrequencyMhz = initialFrequencyMhz;
                InitialSymbolRateKsps = initialSymbolRateKsps;
            }

            public int StreamGeneration;
            public TunerSlot Slot { get; }
            public int Number { get; }
            public MiniTiounerDevice Device { get; }
            public ReceiverService Service { get; }
            public double InitialFrequencyMhz { get; }
            public uint InitialSymbolRateKsps { get; }

            public object GetState(object video)
            {
                TuneRequest tune = Service.LastRequest;
                TSStatus station = Service.Service;
                TunerStatus status = Service.LastStatus;
                bool locked = !Slot.Parked && Service.Connected && Service.State == ReceiverState.Locked && status != null;
                int standard = status == null ? 0 : status.T1P2_demod_status == 2 ? 2 : status.T1P2_demod_status == 3 ? 1 : 0;
                string mode = null;
                if (locked)
                {
                    var modes = standard == 2 ? lookups.modcod_lookup_dvbs2 : lookups.modcod_lookup_dvbs;
                    modes.TryGetValue(status.T1P2_modcode, out mode);
                }
                string[] modeParts = (mode ?? "").Split(' ');
                return new
                {
                    number = Number,
                    video = video,
                    parked = Slot.Parked,
                    name = Device.Name,
                    serial = Device.Serial,
                    usbDescription = Device.UsbDescription,
                    driverVersion = Device.DriverVersion,
                    connected = Service.Connected,
                    state = Slot.Parked ? "Parked" : Service.State.ToString(),
                    frequencyMhz = tune == null ? (double?)null : tune.RfKhz / 1000.0,
                    symbolRateKsps = tune == null ? (uint?)null : tune.SymbolRateKsps,
                    initialFrequencyMhz = InitialFrequencyMhz,
                    initialSymbolRateKsps = InitialSymbolRateKsps,
                    station = locked && station != null ? station.ServiceName : null,
                    provider = locked && station != null ? station.ServiceProvider : null,
                    mer = locked ? (double?)(status.T1P2_mer / 10.0) : null,
                    inputPower = !Slot.Parked && Service.Connected && status != null ? (short?)status.T1P2_input_power_level : null,
                    actualSymbolRateKsps = locked ? (double?)(status.T1P2_symbol_rate / 1000.0) : null,
                    carrierOffsetHz = locked ? (int?)status.T1P2_frequency_carrier_offset : null,
                    standard = locked ? standard : 0,
                    modulation = modeParts.Length > 0 ? modeParts[0] : null,
                    fec = modeParts.Length > 1 ? modeParts[1] : null,
                    mode = locked && !String.IsNullOrEmpty(mode) ? (standard == 2 ? "DVB-S2 " : "DVB-S ") + mode : null
                };
            }
        }
    }
}
