using QocastPlayer.Receiver;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QocastPlayer.Control
{
    // What the calibration needs from one tuner (the program fills it in; the tests use a simulated tuner).
    internal sealed class CalibrationTarget
    {
        public int Number;
        public string Serial, Name;
        public Action<double, uint> Tune;      // tune to an RF frequency in MHz at a symbol rate in kS/s
        public Func<bool> IsLocked;
        public Func<long?> OffsetHz;           // the carrier offset the tuner reports (null if there is none)
    }

    // Finds the frequency error of each tuner on the beacon (10491.500 MHz, a known frequency):
    // tune away, tune back to the beacon, wait for the lock, read the carrier offset for a few seconds; do this
    // several times and average. The error is saved as the correction of that tuner (see AppSettings).
    // All tuners are measured at the same time. Self-contained: it never throws to the caller.
    internal sealed class Calibrator
    {
        public const double BeaconMhz = 10491.5, AwayMhz = 10493.0;
        public const uint BeaconKsps = 1500, AwayKsps = 500;

        // the times are fields so that the tests can run fast
        public int Rounds = 4, LockTimeoutMs = 12000, SettleMs = 1500, MeasureMs = 4000, AwayMs = 2500, SampleMs = 250, MinSamples = 8;
        public long MaxSpreadHz = 3000;

        private sealed class Progress
        {
            public int Number, Round;
            public string Name = "", Serial = "", Status = "waiting", Message = "";
            public long? MeasuredHz, SpreadHz;
            public int GoodRounds;
        }

        private readonly object _sync = new object();
        private List<Progress> _progress = new List<Progress>();
        private CancellationTokenSource _cancel;
        private bool _running;
        private DateTime? _finishedAt;

        public bool Running { get { lock (_sync) return _running; } }

        // False if a calibration is already running. `finished` is called once, on a background thread, when all tuners are done.
        public bool Start(IList<CalibrationTarget> targets, Action finished)
        {
            lock (_sync)
            {
                if (_running) return false;
                _running = true; _finishedAt = null;
                _cancel = new CancellationTokenSource();
                _progress = targets.Select(t => new Progress { Number = t.Number, Name = t.Name ?? "", Serial = t.Serial ?? "" }).ToList();
            }
            CancellationToken token = _cancel.Token;
            var list = targets.ToList();
            Task.Run(() =>
            {
                try
                {
                    var tasks = list.Select((t, i) => Task.Run(() => Run(t, _progress[i], token))).ToArray();
                    try { Task.WaitAll(tasks); } catch (AggregateException) { }
                }
                finally
                {
                    lock (_sync) { _running = false; _finishedAt = DateTime.Now; }
                    try { if (finished != null) finished(); } catch (Exception ex) { Log.Warning(ex, "Calibration: cleanup failed"); }
                }
            });
            return true;
        }

        public void Cancel()
        {
            CancellationTokenSource source;
            lock (_sync) source = _cancel;
            try { if (source != null) source.Cancel(); } catch (ObjectDisposedException) { }
        }

        public object Snapshot()
        {
            lock (_sync)
            {
                return new
                {
                    running = _running,
                    finishedAt = _finishedAt.HasValue ? _finishedAt.Value.ToString("s") : null,
                    rounds = Rounds,
                    tuners = _progress.Select(p => new
                    {
                        number = p.Number,
                        name = p.Name,
                        status = p.Status,
                        round = p.Round,
                        message = p.Message,
                        measuredKhz = p.MeasuredHz.HasValue ? (double?)Math.Round(p.MeasuredHz.Value / 1000.0, 2) : null,
                        spreadKhz = p.SpreadHz.HasValue ? (double?)Math.Round(p.SpreadHz.Value / 1000.0, 2) : null,
                        correctionKhz = Math.Round(AppSettings.CorrectionHz(p.Serial) / 1000.0, 2)
                    }).ToArray()
                };
            }
        }

        // ---- one tuner ----

        private void Run(CalibrationTarget target, Progress p, CancellationToken token)
        {
            var medians = new List<long>();
            try
            {
                for (int round = 1; round <= Rounds; round++)
                {
                    token.ThrowIfCancellationRequested();
                    Set(p, "away", round, "");
                    target.Tune(AwayMhz, AwayKsps);
                    Pause(AwayMs, token);

                    Set(p, "locking", round, "");
                    target.Tune(BeaconMhz, BeaconKsps);
                    if (!WaitForLock(target, token)) { Set(p, "locking", round, "No lock on the beacon in round " + round); continue; }
                    Pause(SettleMs, token);

                    Set(p, "measuring", round, "");
                    var samples = new List<long>();
                    DateTime until = DateTime.UtcNow.AddMilliseconds(MeasureMs);
                    while (DateTime.UtcNow < until)
                    {
                        long? offset = target.IsLocked() ? target.OffsetHz() : null;
                        if (offset.HasValue) samples.Add(offset.Value);
                        Pause(SampleMs, token);
                    }
                    if (samples.Count < MinSamples) { Set(p, "measuring", round, "The signal was lost while measuring in round " + round); continue; }
                    medians.Add(Median(samples));
                    lock (_sync) { p.GoodRounds = medians.Count; p.MeasuredHz = (long)Math.Round(medians.Average()); }
                }

                if (medians.Count < 2)
                {
                    Set(p, "failed", Rounds, "Not enough good readings. Is the beacon visible, and does this tuner lock on it?");
                    return;
                }
                long mean = (long)Math.Round(medians.Average()), spread = medians.Max() - medians.Min();
                lock (_sync) { p.MeasuredHz = mean; p.SpreadHz = spread; }
                if (spread > MaxSpreadHz)   // the readings disagree: an average of them would be a guess, so nothing is saved
                {
                    Set(p, "failed", Rounds, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "The readings differ by {0:0.#} kHz, so nothing was saved. The signal may be unstable: try again.", spread / 1000.0));
                    return;
                }
                // the tuner was already tuned with its old correction, so what it measures is what is still wrong
                string problem = AppSettings.SetCorrectionHz(p.Serial, AppSettings.CorrectionHz(p.Serial) + mean);
                if (problem != null) { Set(p, "failed", Rounds, problem); return; }
                Set(p, "done", Rounds, "Saved.");
            }
            catch (OperationCanceledException) { Set(p, "cancelled", p.Round, "Cancelled."); }
            catch (Exception ex)
            {
                Log.Warning(ex, "Calibration of tuner {Number} failed", p.Number);
                Set(p, "failed", p.Round, "Could not tune this tuner: " + ex.Message);
            }
        }

        private bool WaitForLock(CalibrationTarget target, CancellationToken token)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(LockTimeoutMs);
            while (DateTime.UtcNow < until)
            {
                if (target.IsLocked() && target.OffsetHz().HasValue) return true;
                Pause(200, token);
            }
            return false;
        }

        private static void Pause(int milliseconds, CancellationToken token)
        {
            if (token.WaitHandle.WaitOne(milliseconds)) throw new OperationCanceledException(token);
        }

        private void Set(Progress p, string status, int round, string message)
        {
            lock (_sync) { p.Status = status; p.Round = round; if (message.Length > 0 || status == "done" || status == "failed") p.Message = message; }
        }

        private static long Median(List<long> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int middle = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        }
    }
}
