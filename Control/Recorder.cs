using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace QocastPlayer.Control
{
    // Records every transmission a tuner is locked on (the beacon excepted) as a raw MPEG transport stream:
    //   <Recordings>\<CALLSIGN>\yyyy-MM-dd_HH-mm-ss.ts
    // The callsign comes from the service name the stream announces; without one the folder is NOCALLSIGN.
    // Recordings shorter than MinSeconds are deleted. Self-contained: Feed() never blocks (the disk is written by
    // a background thread) and never throws, and with the switch off it does nothing.
    internal sealed class Recorder : IDisposable
    {
        private const int PendingLimitBytes = 16 << 20, QueueChunks = 4000, GraceSeconds = 3, NameWaitSeconds = 12;
        private const long MinFreeBytes = 1L << 30;

        // A callsign: 1-3 letters/digits, a digit, 1-4 letters (HB9IIU, DK0TE, 2E0ILY, A71A, F8TRT, DM2CFL, EA3FL).
        private static readonly Regex CallsignPattern = new Regex(@"(?<![A-Z0-9])([A-Z0-9]{1,3}[0-9][A-Z]{1,4})(?![A-Z0-9])", RegexOptions.Compiled);

        private sealed class Session
        {
            public int Number;
            public bool Active, Open, Pending;
            public DateTime Start, LastActive, EndedAt, LastDataAt;
            public List<byte[]> PendingData = new List<byte[]>();
            public long PendingBytes, Bytes;
            public string Callsign = "", ServiceName = "", Path = "";
            public BlockingCollection<byte[]> Queue;
            public Thread Writer;
        }

        private readonly string _folder;
        private readonly int _minSeconds;
        private readonly object _sync = new object();
        private readonly Dictionary<int, Session> _sessions = new Dictionary<int, Session>();
        private readonly List<Thread> _finishing = new List<Thread>();
        private volatile bool _enabled;
        private string _warning = "";
        private DateTime _lastSpaceCheck = DateTime.MinValue, _lastLog = DateTime.MinValue;

        public Recorder(string folder, string settingsPath, int minSeconds = 10)
        {
            // The recorder always starts switched off, whatever it was before: it must never begin writing files by surprise.
            _folder = folder; _minSeconds = minSeconds;
        }

        public bool Enabled { get { return _enabled; } }

        public void SetEnabled(bool enabled)
        {
            lock (_sync)
            {
                _warning = "";
                if (enabled && !EnoughSpace()) { _warning = "Not enough free disk space to record"; enabled = false; }
                _enabled = enabled;
                if (!enabled) foreach (Session s in _sessions.Values) End(s);
                Log.Information("Recorder {State}", _enabled ? "on" : "off");
            }
        }

        // Called about once a second for every tuner: is it locked on something worth recording (not parked, not the beacon)?
        public void Tick(int number, bool active, string serviceName)
        {
            if (!_enabled) return;
            try
            {
                lock (_sync)
                {
                    Session s = Get(number);
                    DateTime now = DateTime.UtcNow;
                    s.Active = active;
                    s.ServiceName = serviceName ?? "";
                    if (active) s.LastActive = now;
                    else if ((s.Open || s.Pending) && now - s.LastActive > TimeSpan.FromSeconds(GraceSeconds)) End(s);

                    if (s.Pending && (s.ServiceName.Length > 0 || now - s.Start > TimeSpan.FromSeconds(NameWaitSeconds))) OpenFile(s);
                    else if (s.Open && s.ServiceName.Length > 0 && s.Callsign != CallsignOf(s.ServiceName)) End(s);   // another station: a new recording

                    if (now - _lastSpaceCheck > TimeSpan.FromSeconds(30))
                    {
                        _lastSpaceCheck = now;
                        if (!EnoughSpace())
                        {
                            _warning = "Recording stopped: less than 1 GB free on the disk";
                            _enabled = false;
                            foreach (Session other in _sessions.Values) End(other);
                            Log.Warning("Recorder stopped: low disk space");
                        }
                    }
                }
            }
            catch (Exception ex) { Warn(ex, "Recorder tick failed"); }
        }

        // The bytes just read from tuner `number`; count == 0 means the stream starts again (a retune).
        public void Feed(int number, byte[] data, int count)
        {
            if (!_enabled) return;
            try
            {
                Session s;
                lock (_sync) { if (!_sessions.TryGetValue(number, out s)) { if (count <= 0) return; s = Get(number); } }
                lock (s)
                {
                    if (count <= 0) { End(s); return; }
                    if (!s.Open && !s.Pending)
                    {
                        if (!s.Active) return;
                        s.Pending = true; s.Start = DateTime.Now; s.PendingData.Clear(); s.PendingBytes = 0; s.LastActive = DateTime.UtcNow;
                    }
                    s.LastDataAt = DateTime.Now;
                    var copy = new byte[count];
                    Buffer.BlockCopy(data, 0, copy, 0, count);
                    if (s.Open) { if (!s.Queue.TryAdd(copy)) Warn(null, "Recorder: the disk cannot keep up, data dropped"); }
                    else
                    {
                        s.PendingData.Add(copy); s.PendingBytes += count;
                        if (s.PendingBytes > PendingLimitBytes) OpenFile(s);   // the name never came: do not hold more in memory
                    }
                }
            }
            catch (Exception ex) { Warn(ex, "Recorder feed failed"); }
        }

        public object Snapshot()
        {
            lock (_sync)
            {
                long free = FreeBytes();
                return new
                {
                    enabled = _enabled,
                    folder = _folder,
                    warning = _warning,
                    freeGb = free < 0 ? (double?)null : Math.Round(free / 1073741824.0, 1),
                    recording = _sessions.Values.Where(s => s.Open).Select(s => new
                    {
                        number = s.Number,
                        callsign = s.Callsign,
                        since = s.Start.ToString("s"),
                        megabytes = Math.Round(Interlocked.Read(ref s.Bytes) / 1048576.0, 1)
                    }).ToArray(),
                    waiting = _sessions.Values.Where(s => s.Pending && !s.Open).Select(s => s.Number).ToArray()
                };
            }
        }

        public void Dispose()
        {
            _enabled = false;
            Thread[] threads;
            lock (_sync)
            {
                foreach (Session s in _sessions.Values) End(s);
            }
            lock (_finishing) threads = _finishing.ToArray();
            foreach (Thread t in threads) t.Join(3000);
        }

        // ---- the callsign ----
        internal static string CallsignOf(string serviceName)
        {
            if (string.IsNullOrWhiteSpace(serviceName)) return "NOCALLSIGN";
            string text = serviceName.ToUpperInvariant().Replace('Ø', '0').Replace('Ö', 'O').Replace('Ü', 'U').Replace('Ä', 'A');
            Match m = CallsignPattern.Match(text);
            return m.Success ? m.Groups[1].Value : "NOCALLSIGN";
        }

        // ---- internals ----
        private Session Get(int number)
        {
            Session s;
            if (!_sessions.TryGetValue(number, out s)) _sessions[number] = s = new Session { Number = number };
            return s;
        }

        // Pending data goes to disk now that the name (or the time limit) has decided the folder.
        private void OpenFile(Session s)
        {
            lock (s)
            {
                if (!s.Pending || s.Open) return;
                s.Callsign = CallsignOf(s.ServiceName);
                try
                {
                    string directory = Path.Combine(_folder, s.Callsign);
                    Directory.CreateDirectory(directory);
                    string stamp = s.Start.ToString("yyyy-MM-dd_HH-mm-ss"), path = Path.Combine(directory, stamp + ".ts");
                    for (int n = 2; File.Exists(path); n++) path = Path.Combine(directory, stamp + "_" + n + ".ts");
                    s.Path = path;
                }
                catch (Exception ex) { Warn(ex, "Recorder: could not create the folder"); s.Pending = false; s.PendingData.Clear(); return; }

                s.Queue = new BlockingCollection<byte[]>(QueueChunks);
                // begin on a packet boundary, so that any player can open the file
                byte[] all = new byte[s.PendingBytes]; int at = 0;
                foreach (byte[] chunk in s.PendingData) { Buffer.BlockCopy(chunk, 0, all, at, chunk.Length); at += chunk.Length; }
                int start = 0;
                for (int i = 0; i + 188 < all.Length; i++) if (all[i] == 0x47 && all[i + 188] == 0x47) { start = i; break; }
                if (all.Length - start > 0)
                {
                    var first = new byte[all.Length - start]; Buffer.BlockCopy(all, start, first, 0, first.Length); s.Queue.Add(first);
                }
                s.PendingData.Clear(); s.PendingBytes = 0;
                s.Pending = false; s.Open = true; s.Bytes = 0;
                var writer = new Thread(() => Write(s, s.Queue, s.Path, s.Start)) { Name = "Recorder " + s.Number, IsBackground = false };
                s.Writer = writer;
                lock (_finishing) { _finishing.RemoveAll(t => !t.IsAlive); _finishing.Add(writer); }
                writer.Start();
                Log.Information("Recording tuner {Number} as {Callsign}: {Path}", s.Number, s.Callsign, s.Path);
            }
        }

        private void End(Session s)
        {
            lock (s)
            {
                s.EndedAt = DateTime.Now;
                if (s.Open) { s.Open = false; try { s.Queue.CompleteAdding(); } catch (ObjectDisposedException) { } s.Queue = null; }
                s.Pending = false; s.PendingData.Clear(); s.PendingBytes = 0;
            }
        }

        private void Write(Session s, BlockingCollection<byte[]> queue, string path, DateTime start)
        {
            long total = 0;
            try
            {
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16))
                {
                    DateTime flushed = DateTime.UtcNow;
                    foreach (byte[] chunk in queue.GetConsumingEnumerable())
                    {
                        file.Write(chunk, 0, chunk.Length);
                        total += chunk.Length;
                        Interlocked.Exchange(ref s.Bytes, total);
                        if (DateTime.UtcNow - flushed > TimeSpan.FromSeconds(1)) { file.Flush(); flushed = DateTime.UtcNow; }
                    }
                }
            }
            catch (Exception ex) { Warn(ex, "Recorder: writing failed"); }
            finally
            {
                try
                {
                    DateTime ended = s.LastDataAt > start ? s.LastDataAt : start;   // the length of the recording itself, without the wait after the signal ended
                    if ((ended - start).TotalSeconds < _minSeconds && File.Exists(path))
                    {
                        File.Delete(path);
                        Log.Information("Recording {Path} was shorter than {Seconds} s and was removed", path, _minSeconds);
                    }
                    else Log.Information("Recording finished: {Path} ({Megabytes:0.0} MB)", path, total / 1048576.0);
                }
                catch (Exception ex) { Log.Warning(ex, "Recorder: could not tidy up {Path}", path); }
            }
        }

        private long FreeBytes()
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(_folder));
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch { return -1; }
        }

        private bool EnoughSpace() { long free = FreeBytes(); return free < 0 || free >= MinFreeBytes; }

        private void Warn(Exception ex, string text)
        {
            if (DateTime.UtcNow - _lastLog < TimeSpan.FromSeconds(30)) return;
            _lastLog = DateTime.UtcNow;
            if (ex != null) Log.Warning(ex, text); else Log.Warning(text);
        }
    }
}
