using opentuner;
using opentuner.MediaSources.Minitiouner.HardwareInterfaces;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace QocastPlayer.Receiver
{
    // One tuner device with its control thread (NimThread): the hardware, the queue of tune requests and the
    // status that the thread reads. A MiniTiouner has a board of its own. A PicoTuner has two tuners on one USB
    // device and one I2C bus, so both of its ReceiverServices share one board (see AcquirePico).
    internal sealed class TunerBoard
    {
        private const int ThreadStopMs = 3000;
        private static readonly object PicoSync = new object();
        private static readonly System.Collections.Generic.Dictionary<string, TunerBoard> Picos = new System.Collections.Generic.Dictionary<string, TunerBoard>();

        private readonly NimThread _nim;
        private readonly Thread _nimThread;
        private int _users;                 // PicoSync must be held
        private string _picoKey;            // set for a PicoTuner board; PicoSync must be held
        private volatile bool _lost;

        public MTHardwareInterface Hardware { get; }
        public string Name { get; }
        public ConcurrentQueue<TunerConfig> ConfigQueue { get; } = new ConcurrentQueue<TunerConfig>();

        // raised on the control thread after every status read; the status holds both tuners
        public event Action<TunerStatus> StatusRead;

        // raised once when the tuner stops answering or is unplugged
        public event Action Lost;

        public bool IsLost { get { return _lost; } }

        public TunerBoard(MTHardwareInterface hardware, string name)
        {
            Hardware = hardware;
            Name = name;
            _nim = new NimThread(ConfigQueue, hardware, status => StatusRead?.Invoke(status), OnHardwareLost);
            _nimThread = new Thread(_nim.worker_thread) { Name = "Nim", IsBackground = true };
            _nimThread.Start();
        }

        public void Wake()
        {
            _nim.Wake();
        }

        private void OnHardwareLost()
        {
            _lost = true;
            Lost?.Invoke();
        }

        // ---- who uses the board ----

        // A board that only one ReceiverService uses (MiniTiouner).
        public TunerBoard Retain()
        {
            lock (PicoSync)
                _users++;
            return this;
        }

        // The board of one PicoTuner (`key` tells which one): the first caller opens it with `open`, the second one joins it.
        // null if it could not be opened, or if it was lost and is still being let go of by the other tuner
        // (the caller tries again later).
        public static TunerBoard AcquirePico(string key, Func<TunerBoard> open)
        {
            lock (PicoSync)
            {
                TunerBoard board;
                Picos.TryGetValue(key, out board);
                if (board != null && board._lost)
                    return null;
                if (board == null)
                {
                    board = open();
                    if (board == null)
                        return null;
                    board._picoKey = key;
                    Picos[key] = board;
                }
                board._users++;
                return board;
            }
        }

        // The last user closes the hardware.
        public void Release()
        {
            bool last;
            lock (PicoSync)
            {
                _users--;
                last = _users <= 0;
                TunerBoard registered;
                if (last && _picoKey != null && Picos.TryGetValue(_picoKey, out registered) && ReferenceEquals(registered, this))
                    Picos.Remove(_picoKey);
            }
            if (last)
                Close();
        }

        private void Close()
        {
            _nim.Stop();
            if (!_nimThread.Join(ThreadStopMs))
                Log.Warning("Receiver: nim thread did not stop in time");

            try
            {
                Hardware.hw_ts_led(0, false);
            }
            catch { }   // tuner may already be unplugged

            try
            {
                Hardware.hw_close();
            }
            catch (Exception ex)
            {
                Log.Warning("Receiver: close: " + ex.Message);
            }
            Log.Information("Receiver: tuner released");
        }
    }

    // The status that the control thread reads holds both tuners (T1P2_ = tuner 1, T2P1_ = tuner 2).
    // The rest of the program reads one tuner's values as T1P2_, so tuner 2's are copied there.
    internal static class TunerStatusView
    {
        public static TunerStatus For(TunerStatus status, int tuner)
        {
            if (tuner != 2)
                return status;

            return new TunerStatus
            {
                lna_top_ok = status.lna_top_ok,
                lna_bottom_ok = status.lna_bottom_ok,
                errors_ldpc_count = status.errors_ldpc_count,
                T1P2_demod_status = status.T2P1_demod_status,
                T1P2_ts_status = status.T2P1_ts_status,
                T1P2_stream_format = status.T2P1_stream_format,
                T1P2_lna_gain = status.T2P1_lna_gain,
                T1P2_power_i = status.T2P1_power_i,
                T1P2_power_q = status.T2P1_power_q,
                T1P2_mer = status.T2P1_mer,
                T1P2_short_frame = status.T2P1_short_frame,
                T1P2_pilots = status.T2P1_pilots,
                T1P2_frequency_carrier_offset = status.T2P1_frequency_carrier_offset,
                T1P2_symbol_rate = status.T2P1_symbol_rate,
                T1P2_modcode = status.T2P1_modcode,
                T1P2_puncture_rate = status.T2P1_puncture_rate,
                T1P2_errors_bch_uncorrected = status.T2P1_errors_bch_uncorrected,
                T1P2_viterbi_error_rate = status.T2P1_viterbi_error_rate,
                T1P2_ber = status.T2P1_ber,
                T1P2_errors_bch_count = status.T2P1_errors_bch_count,
                T1P2_agc1_gain = status.T2P1_agc1_gain,
                T1P2_agc2_gain = status.T2P1_agc2_gain,
                T1P2_input_power_level = status.T2P1_input_power_level,
                T1P2_build_queue = status.T2P1_build_queue,
                T1P2_reset = status.T2P1_reset,
                T1P2_constellation = status.T2P1_constellation,
                T1P2_rf_input = status.T2P1_rf_input,
                T1P2_requested_frequency = status.T2P1_requested_frequency,
                T1P2_rolloff = status.T2P1_rolloff
            };
        }
    }
}
