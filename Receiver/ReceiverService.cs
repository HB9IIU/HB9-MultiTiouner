using opentuner;
using opentuner.MediaSources.Minitiouner.HardwareInterfaces;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace QocastPlayer.Receiver
{
    public enum ReceiverState
    {
        Disconnected,   // tuner not found / unplugged
        Waiting,        // tuner ready, no frequency from QOCAST yet
        Tuning,         // tuned, no signal locked
        Locked          // signal locked, stream flowing
    }

    // The tuner chosen in player.json: opens it, retries every 5 s while it is missing,
    // tunes it, and copies the received stream into Stream while the signal is locked.
    // Replaces OpenTuner's MinitiounerSource without its panels, recorders and streamers.
    public sealed class ReceiverService : IDisposable
    {
        public const uint MinIfKhz = 144000;
        public const uint MaxIfKhz = 2450000;
        private const int RetryMs = 5000;
        private const int ThreadStopMs = 3000;

        private readonly PlayerConfig _config;
        private readonly string _deviceSerial;
        private readonly int _picoTuner;       // 0 = MiniTiouner, 1 or 2 = that tuner of the PicoTuner
        private readonly object _lock = new object();
        private readonly ManualResetEvent _stopEvent = new ManualResetEvent(false);
        private Thread _supervisor;

        // the session currently owning the tuner (null while disconnected)
        private Session _session;

        // last accepted request, re-applied after the tuner is plugged in again
        private TuneRequest _lastRequest;

        private ReceiverState _state = ReceiverState.Disconnected;
        private TunerStatus _lastStatus;

        // station name etc. from the stream (OpenTuner's TSParserThread, fed while locked)
        private readonly TSParserThread _parser;
        private TSStatus _service;

        // received transport stream, read by the video player; oldest bytes are dropped when full
        public CircularBuffer Stream { get; } = new CircularBuffer(GlobalDefines.CircularBufferStartingCapacity);

        // optional copy of the received bytes (stream out); (null, 0) means the stream starts again
        public Action<byte[], int> TsTap;

        // raised on a worker thread
        public event Action<ReceiverState> StateChanged;

        public ReceiverService(PlayerConfig config, string deviceSerial = null, int picoTuner = 0)
        {
            _config = config;
            _deviceSerial = deviceSerial;
            _picoTuner = picoTuner;
            _parser = new TSParserThread(OnService);
        }

        public string HardwareName { get; private set; }

        public ReceiverState State
        {
            get { lock (_lock) return _state; }
        }

        public TuneRequest LastRequest
        {
            get { lock (_lock) return _lastRequest; }
        }

        public TunerStatus LastStatus
        {
            get { lock (_lock) return _lastStatus; }
        }

        public bool Connected
        {
            get { lock (_lock) return _session != null; }
        }

        private void OnService(TSStatus status)
        {
            TSStatus previous;
            lock (_lock)
            {
                previous = _service;
                _service = status;
            }
            if (previous == null || previous.ServiceName != status.ServiceName)
                Log.Information("Receiver: station {Name} ({Provider})", status.ServiceName, status.ServiceProvider);
        }

        // service name / provider of the locked station, null until known
        public TSStatus Service
        {
            get { lock (_lock) return _service; }
        }

        public void Start()
        {
            // runs for the whole player life (it has no stop of its own), never cleared:
            // clearing it while it waits inside a packet would make it spin
            new Thread(_parser.worker_thread) { Name = "TS parser", IsBackground = true }.Start();

            _supervisor = new Thread(SupervisorLoop) { Name = "Receiver", IsBackground = true };
            _supervisor.Start();
        }

        // RF frequency as QOCAST sends it; the LNB frequency (settings.json, else player.json) is taken off here.
        // direct = QOCAST's local mode: the frequency goes to the tuner as it is (the Pluto's
        // uplink, no LNB), and LNB power stays off.
        // Returns null when the tuner can receive it, otherwise a short error code for the API.
        public string CheckTune(long rfKhz, long symbolRateKsps, bool direct = false)
        {
            long ifKhz = direct ? rfKhz : AppSettings.IfKhz(rfKhz, _deviceSerial);
            if (ifKhz < MinIfKhz || ifKhz > MaxIfKhz)
                return "invalid_frequency";
            if (symbolRateKsps < 1 || symbolRateKsps > 10000)
                return "invalid_symbol_rate";
            return null;
        }

        public void Tune(long rfKhz, long symbolRateKsps, bool direct = false)
        {
            string error = CheckTune(rfKhz, symbolRateKsps, direct);
            if (error != null)
                throw new ArgumentOutOfRangeException(nameof(rfKhz), error);

            long ifKhz = direct ? rfKhz : AppSettings.IfKhz(rfKhz, _deviceSerial);
            var request = new TuneRequest(rfKhz, (uint)ifKhz, (uint)symbolRateKsps, direct);
            Session session;
            lock (_lock)
            {
                _lastRequest = request;
                session = _session;
            }

            Log.Information("Tune {Rf} kHz (IF {If} kHz), {Sr} kS/s", rfKhz, ifKhz, symbolRateKsps);
            if (session != null)
                session.Tune(request);
        }

        public void Park()
        {
            Session session;
            lock (_lock) { _lastRequest = null; _lastStatus = null; _service = null; session = _session; }
            session?.Park();
            Stream.Clear();
            SetState(session == null ? ReceiverState.Disconnected : ReceiverState.Waiting);
        }

        public void Dispose()
        {
            _stopEvent.Set();
            if (_supervisor != null && !_supervisor.Join(ThreadStopMs * 3))
                Log.Warning("Receiver: supervisor did not stop in time");
        }

        private void SupervisorLoop()
        {
            while (!_stopEvent.WaitOne(0))
            {
                Session session = Session.Open(this);
                if (session == null)
                {
                    SetState(ReceiverState.Disconnected);
                    if (_stopEvent.WaitOne(RetryMs))
                        break;
                    continue;
                }

                TuneRequest resume;
                lock (_lock)
                {
                    _session = session;
                    resume = _lastRequest;
                }
                HardwareName = session.HardwareName;
                SetState(resume == null ? ReceiverState.Waiting : ReceiverState.Tuning);
                if (resume != null)
                    session.Tune(resume);

                // until the tuner is lost or the player closes
                WaitHandle.WaitAny(new WaitHandle[] { _stopEvent, session.Lost });

                lock (_lock)
                    _session = null;
                session.Close();
                Stream.Clear();

                if (!_stopEvent.WaitOne(0))
                {
                    Log.Warning("Receiver: tuner lost, retrying");
                    SetState(ReceiverState.Disconnected);
                    if (_stopEvent.WaitOne(RetryMs))
                        break;
                }
            }
            SetState(ReceiverState.Disconnected);
        }

        private void SetState(ReceiverState state)
        {
            lock (_lock)
            {
                if (_state == state)
                    return;
                _state = state;
            }
            Log.Information("Receiver: " + state);
            StateChanged?.Invoke(state);
        }

        // ---- one connection to the tuner, from open to unplug/close ----

        private sealed class Session
        {
            private readonly ReceiverService _owner;
            private readonly TunerBoard _board;
            private readonly MTHardwareInterface _hardware;
            private readonly int _tuner;            // 1 or 2: which tuner of the board this session drives
            private readonly int _tsDevice;         // the transport stream of that tuner
            private readonly Action _boardLost;
            private readonly Action<TunerStatus> _boardStatus;
            private readonly Thread _tsThread;
            private readonly ManualResetEvent _lost = new ManualResetEvent(false);
            private readonly AutoResetEvent _tsWake = new AutoResetEvent(false);

            private volatile bool _closing;
            private volatile bool _locked;
            private volatile bool _awaitingRetune;
            private volatile bool _parked = true;
            private readonly object _commandSync = new object();

            // Unsteady lock (it keeps dropping): the demodulator can lock beside the signal
            // (e.g. half a symbol rate off) and stay stuck there. A fresh search fixes it.
            private const int UnsteadyLosses = 3;
            private static readonly TimeSpan UnsteadyWindow = TimeSpan.FromSeconds(15);
            private readonly Queue<DateTime> _lockLosses = new Queue<DateTime>();
            private DateTime _lastFreshSearch = DateTime.MinValue;
            private volatile bool _streaming;

            public string HardwareName { get; }
            public WaitHandle Lost { get { return _lost; } }

            private Session(ReceiverService owner, TunerBoard board, int tuner)
            {
                _owner = owner;
                _board = board;
                _hardware = board.Hardware;
                _tuner = tuner;
                _tsDevice = tuner == 2 ? PicoTunerInterface.TS1 : PicoTunerInterface.TS2;
                HardwareName = board.Name;

                _tsThread = new Thread(TsLoop) { Name = "TS" + tuner, IsBackground = true };

                _hardware.hw_ts_led(0, false);
                _hardware.hw_ts_led(1, false);

                _boardLost = () => _lost.Set();
                _boardStatus = status => OnNimStatus(TunerStatusView.For(status, _tuner));
                board.Lost += _boardLost;
                board.StatusRead += _boardStatus;
                if (board.IsLost)       // it was lost before this session listened
                    _lost.Set();

                _tsThread.Start();
            }

            // same steps as OpenTuner's MinitiounerSource.hardware_init, one tuner only
            private enum Attempt
            {
                MiniTiouner,            // FTDI ports recognised by their names
                PicoTuner,
                MiniTiounerFallback     // FTDI ports with unknown names: 0 and 1, as OpenTuner does
            }

            // receiver_type "auto" (default): MiniTiouner, then PicoTuner, then unknown MiniTiouner variants
            public static Session Open(ReceiverService owner)
            {
                Attempt[] attempts;
                if (owner._deviceSerial != null)
                {
                    attempts = new[] { owner._picoTuner > 0 ? Attempt.PicoTuner : Attempt.MiniTiouner };
                }
                else switch (owner._config.receiver_type)
                {
                    case "minitiouner": attempts = new[] { Attempt.MiniTiouner, Attempt.MiniTiounerFallback }; break;
                    case "picotuner": attempts = new[] { Attempt.PicoTuner }; break;
                    default: attempts = new[] { Attempt.MiniTiouner, Attempt.PicoTuner, Attempt.MiniTiounerFallback }; break;
                }

                foreach (Attempt attempt in attempts)
                {
                    TunerBoard board;
                    if (attempt == Attempt.PicoTuner && owner._deviceSerial != null)
                        board = TunerBoard.AcquirePico(() => TryOpen(owner, attempt));      // both tuners share one board
                    else
                        board = TryOpen(owner, attempt)?.Retain();
                    if (board != null)
                        return new Session(owner, board, owner._picoTuner == 2 ? 2 : 1);
                }
                Log.Information("Receiver: no tuner found");
                return null;
            }

            private static TunerBoard TryOpen(ReceiverService owner, Attempt attempt)
            {
                MTHardwareInterface hardware = attempt == Attempt.PicoTuner
                    ? (MTHardwareInterface)new PicoTunerInterface()
                    : owner._deviceSerial == null
                        ? (MTHardwareInterface)new FTDIInterface()
                        : new FTDIInterface(owner._deviceSerial);

                try
                {
                    uint i2c_port = 99, ts_port = 99, ts_port2 = 99;
                    string name = "Unknown";

                    byte err = hardware.hw_detect(ref i2c_port, ref ts_port, ref ts_port2, ref name);
                    if (attempt == Attempt.MiniTiounerFallback)
                    {
                        if (err != 0)           // fewer than two FTDI ports: no MiniTiouner at all
                        {
                            hardware.hw_close();
                            return null;
                        }
                        i2c_port = 0;
                        ts_port = 1;
                        name = "MiniTiouner (unknown variant)";
                    }
                    else if (i2c_port == 99 || ts_port == 99)
                    {
                        hardware.hw_close();
                        return null;
                    }

                    Log.Information("Receiver: opening " + name + " (i2c " + i2c_port + ", ts " + ts_port + ")");
                    // MiniTiouner: the second stream port (tuner 2) is not opened, the player uses its tuner 1 only.
                    // PicoTuner: hw_init opens both streams whatever the arguments.
                    err = hardware.hw_init(i2c_port, ts_port, 99);
                    if (err != 0)
                    {
                        if (attempt != Attempt.PicoTuner)       // PicoTuner: "not plugged in" ends here too
                            Log.Warning("Receiver: open failed, error " + err);
                        hardware.hw_close();
                        return null;
                    }

                    return new TunerBoard(hardware, name);
                }
                catch (Exception ex)
                {
                    // missing driver DLL, device busy (another program has the tuner), ...
                    Log.Warning("Receiver: open failed: " + ex.Message);
                    try { hardware.hw_close(); } catch { }
                    return null;
                }
            }

            public void Tune(TuneRequest request)
            {
                lock (_commandSync)
                {
                if (!Object.ReferenceEquals(_owner.LastRequest, request)) return;
                PlayerConfig config = _owner._config;
                bool inputB = config.rf_input == "B";
                byte psu = config.lnb_power == "vertical" ? (byte)1 : config.lnb_power == "horizontal" ? (byte)2 : (byte)0;
                if (request.Direct)
                    psu = 0;        // local mode: an antenna or the Pluto on the input, never LNB voltage

                var tunerConfig = new TunerConfig
                {
                    tuner = (byte)_tuner,
                    frequency = request.IfKhz,
                    symbol_rate = request.SymbolRateKsps,
                    rf_input = inputB ? nim.NIM_INPUT_BOTTOM : nim.NIM_INPUT_TOP,
                    tone_22kHz_P1 = config.tone_22khz,
                    lnba_psu = inputB ? (byte)0 : psu,
                    lnbb_psu = inputB ? psu : (byte)0
                };

                // a new station: old stream bytes must not reach the player, and status
                // reads still showing the old station are ignored until the tuner was set
                _parked = false;
                _awaitingRetune = true;
                StopStreaming();
                _locked = false;
                _owner.SetState(ReceiverState.Tuning);

                _board.ConfigQueue.Enqueue(tunerConfig);
                _board.Wake();
            
                }
            }

            public void Park()
            {
                lock (_commandSync)
                {
                _parked = true;
                StopStreaming();
                _locked = false;
            
                }
            }

            private void OnNimStatus(TunerStatus status)
            {
                lock (_commandSync)
                {
                if (_closing || _parked)
                    return;

                lock (_owner._lock)
                    _owner._lastStatus = status;

                // T1P2_reset marks the first status read after the tuner was set
                if (_awaitingRetune)
                {
                    if (!status.T1P2_reset)
                        return;
                    _awaitingRetune = false;
                }

                bool locked = status.T1P2_demod_status >= 2 && !status.T1P2_reset;
                if (locked == _locked)
                    return;

                _locked = locked;
                Log.Information("Receiver: lock " + (locked ? "on" : "off"));
                _hardware.hw_ts_led(_tuner - 1, locked);

                if (locked)
                    StartStreaming();
                else
                    StopStreaming();

                _owner.SetState(locked ? ReceiverState.Locked : ReceiverState.Tuning);

                if (!locked)
                    SearchAgainIfUnsteady();
            
                }
            }

            private void SearchAgainIfUnsteady()
            {
                DateTime now = DateTime.UtcNow;
                _lockLosses.Enqueue(now);
                while (_lockLosses.Count > 0 && now - _lockLosses.Peek() > UnsteadyWindow)
                    _lockLosses.Dequeue();
                if (_lockLosses.Count < UnsteadyLosses || now - _lastFreshSearch < UnsteadyWindow)
                    return;

                TuneRequest request = _owner.LastRequest;
                if (request == null)
                    return;
                _lastFreshSearch = now;
                _lockLosses.Clear();
                Log.Information("Receiver: lock keeps dropping, searching again");
                Tune(request);
            }

            private void StartStreaming()
            {
                _owner.Stream.Clear();
                _streaming = true;
                _tsWake.Set();
            }

            private void StopStreaming()
            {
                _streaming = false;
                _owner.Stream.Clear();
                lock (_owner._lock)
                    _owner._service = null;
            }

            // replaces OpenTuner's TSThread: reads only while locked, stops when asked
            private void TsLoop()
            {
                byte[] data = new byte[4096];
                bool reading = false;
                long rateBytes = 0;
                DateTime rateSince = DateTime.UtcNow;

                try
                {
                    while (!_closing)
                    {
                        if (!_streaming)
                        {
                            reading = false;
                            _tsWake.WaitOne(500);
                            continue;
                        }

                        if (!reading)
                        {
                            // throw away what the tuner buffered before the lock
                            _hardware.transport_flush(_tsDevice);
                            _owner.Stream.Clear();
                            _owner.TsTap?.Invoke(null, 0);
                            reading = true;
                            rateBytes = 0;
                            rateSince = DateTime.UtcNow;
                        }

                        uint dataRead = 0;
                        if (_hardware.transport_read(_tsDevice, ref data, ref dataRead) != 0)
                        {
                            Thread.Sleep(10);
                            continue;
                        }

                        if (!_streaming || _closing || _parked)
                            continue;

                        CircularBuffer parserQueue = _owner._parser.parser_ts_data_queue;
                        for (int c = 0; c < dataRead; c++)
                        {
                            _owner.Stream.Enqueue(data[c]);
                            parserQueue.Enqueue(data[c]);
                        }
                        _owner.TsTap?.Invoke(data, (int)dataRead);

                        // stream rate in the log every 10 s, to see that data flows
                        rateBytes += dataRead;
                        double seconds = (DateTime.UtcNow - rateSince).TotalSeconds;
                        if (seconds >= 10)
                        {
                            Log.Information("Receiver: stream {Rate} kbit/s", (int)(rateBytes * 8 / seconds / 1000));
                            rateBytes = 0;
                            rateSince = DateTime.UtcNow;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!_closing)
                    {
                        Log.Error(ex, "Receiver: stream read stopped");
                        _lost.Set();
                    }
                }
            }

            public void Close()
            {
                _closing = true;
                _streaming = false;
                _board.Lost -= _boardLost;
                _board.StatusRead -= _boardStatus;
                _tsWake.Set();

                if (!_tsThread.Join(ThreadStopMs))
                    Log.Warning("Receiver: TS thread did not stop in time");

                // the last session to let go stops the control thread and closes the hardware
                _board.Release();
            }
        }
    }

    public sealed class TuneRequest
    {
        public TuneRequest(long rfKhz, uint ifKhz, uint symbolRateKsps, bool direct = false)
        {
            RfKhz = rfKhz;
            IfKhz = ifKhz;
            SymbolRateKsps = symbolRateKsps;
            Direct = direct;
        }

        public long RfKhz { get; }
        public uint IfKhz { get; }
        public uint SymbolRateKsps { get; }
        public bool Direct { get; }
    }
}
