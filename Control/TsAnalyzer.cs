using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace QocastPlayer.Control
{
    // A small MPEG transport stream analyser for one tuner: per-PID packets and bitrate, continuity errors, PAT / PMT / SDT.
    // Self-contained: Feed() is called from the tuner's read loop, never throws, and does only light work per packet.
    internal sealed class TsAnalyzer
    {
        private const int Packet = 188;

        private sealed class PidInfo
        {
            public long Packets, WindowPackets, CcErrors, TeiErrors;
            public int LastCc = -1;
            public bool DuplicateSeen, HasPcr;
            public double Kbps;
            // what the elementary stream says about itself (codec, size, frame rate, audio format)
            public string Info = "";
            public bool InfoDone, NextIsCode;
            public int ZeroRun, CapKind, CapLen;
            public byte[] Cap;
        }

        // reads bits from a byte array (H.264 / H.265 parameter sets)
        private sealed class Bits
        {
            private readonly byte[] _d; private readonly int _bits; private int _pos;
            public Bits(byte[] data, int length) { _d = data; _bits = length * 8; }
            public int Bit() { if (_pos >= _bits) { _pos++; return 0; } int v = (_d[_pos >> 3] >> (7 - (_pos & 7))) & 1; _pos++; return v; }
            public uint Read(int n) { uint v = 0; for (int i = 0; i < n; i++) v = (v << 1) | (uint)Bit(); return v; }
            public uint Ue() { int zeros = 0; while (_pos < _bits && Bit() == 0 && zeros < 31) zeros++; return zeros == 0 ? 0 : ((1u << zeros) - 1) + Read(zeros); }
            public int Se() { uint k = Ue(); return (k & 1) != 0 ? (int)((k + 1) / 2) : -(int)(k / 2); }
            public bool Overrun { get { return _pos > _bits; } }
        }

        private sealed class Section { public byte[] Data = new byte[4096]; public int Length; }

        private sealed class EsInfo { public int Pid, Type; public string Language = "", Extra = ""; }

        private sealed class Program
        {
            public int Number, PmtPid, PcrPid = -1;
            public List<EsInfo> Streams = new List<EsInfo>();
        }

        private readonly object _sync = new object();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Dictionary<int, PidInfo> _pids = new Dictionary<int, PidInfo>();
        private readonly Dictionary<int, Section> _sections = new Dictionary<int, Section>();
        private readonly Dictionary<int, Program> _programs = new Dictionary<int, Program>();          // by program number
        private readonly Dictionary<int, int> _pmtPids = new Dictionary<int, int>();                   // PMT pid -> program number
        private readonly Dictionary<int, int> _esTypes = new Dictionary<int, int>();                  // elementary pid -> stream type
        private readonly Dictionary<int, string> _esExtra = new Dictionary<int, string>();
        private readonly Dictionary<int, string[]> _services = new Dictionary<int, string[]>();        // service id -> { name, provider, type }
        private byte[] _buffer = new byte[Packet * 64];
        private int _length;
        private bool _synced;
        private long _packets, _nulls, _teiErrors, _syncLosses, _ccErrors, _windowTotal, _windowNulls, _lastDataMs;
        private long _windowStartMs;
        private double _totalKbps, _nullKbps;
        private DateTime _lastWarning = DateTime.MinValue;

        // (null, 0) means the stream starts again: forget everything.
        public void Feed(byte[] data, int count)
        {
            try
            {
                lock (_sync)
                {
                    if (count <= 0 || data == null) { Reset(); return; }
                    if (_length + count > _buffer.Length) Array.Resize(ref _buffer, _length + count + Packet * 16);
                    Buffer.BlockCopy(data, 0, _buffer, _length, count);
                    _length += count;
                    Process();
                    long now = _clock.ElapsedMilliseconds;
                    _lastDataMs = now;
                    if (now - _windowStartMs >= 1000) Rates(now);
                }
            }
            catch (Exception ex)
            {
                if (DateTime.UtcNow - _lastWarning > TimeSpan.FromSeconds(30)) { _lastWarning = DateTime.UtcNow; Log.Warning(ex, "TS analyser failed"); }
            }
        }

        // The name of the first service that has announced one (empty until the SDT has been seen).
        public string ServiceName()
        {
            lock (_sync)
            {
                foreach (Program program in _programs.Values.OrderBy(x => x.Number))
                {
                    string[] service;
                    if (_services.TryGetValue(program.Number, out service) && service[0].Length > 0) return service[0];
                }
                foreach (string[] service in _services.Values) if (service[0].Length > 0) return service[0];
                return "";
            }
        }

        public object Snapshot()
        {
            lock (_sync)
            {
                long now = _clock.ElapsedMilliseconds;
                bool running = _packets > 0 && now - _lastDataMs < 3000;
                var pmtOf = new Dictionary<int, Program>();
                foreach (Program program in _programs.Values) pmtOf[program.PmtPid] = program;
                var esOf = new Dictionary<int, Tuple<Program, EsInfo>>();
                foreach (Program program in _programs.Values)
                    foreach (EsInfo es in program.Streams) esOf[es.Pid] = Tuple.Create(program, es);

                double total = Math.Max(_totalKbps, 0.001);
                var pids = _pids.OrderBy(p => p.Key).Select(p =>
                {
                    string kind; string detail = "";
                    Tuple<Program, EsInfo> es; Program pmt;
                    if (esOf.TryGetValue(p.Key, out es)) { kind = StreamTypeName(es.Item2.Type); detail = es.Item2.Language.Length > 0 ? es.Item2.Language : es.Item2.Extra; }
                    else if (pmtOf.TryGetValue(p.Key, out pmt)) kind = "PMT";
                    else kind = WellKnown(p.Key, p.Value);
                    return new
                    {
                        pid = p.Key,
                        kind,
                        detail,
                        info = p.Value.Info,
                        packets = p.Value.Packets,
                        kbps = Math.Round(p.Value.Kbps, 1),
                        percent = Math.Round(p.Value.Kbps * 100.0 / total, 1),
                        ccErrors = p.Value.CcErrors,
                        teiErrors = p.Value.TeiErrors
                    };
                }).ToArray();

                var programs = _programs.Values.OrderBy(p => p.Number).Select(program =>
                {
                    string[] service;
                    _services.TryGetValue(program.Number, out service);
                    return new
                    {
                        number = program.Number,
                        pmtPid = program.PmtPid,
                        pcrPid = program.PcrPid,
                        name = service != null ? service[0] : "",
                        provider = service != null ? service[1] : "",
                        serviceType = service != null ? service[2] : ""
                    };
                }).ToArray();

                long ordinary = _packets - _nulls;
                return new
                {
                    running,
                    totals = new
                    {
                        packets = _packets,
                        kbps = Math.Round(_totalKbps, 1),
                        nullKbps = Math.Round(_nullKbps, 1),
                        nullPercent = _totalKbps > 0 ? Math.Round(_nullKbps * 100.0 / _totalKbps, 1) : 0.0,
                        ccErrors = _ccErrors,
                        teiErrors = _teiErrors,
                        syncLosses = _syncLosses,
                        usefulPackets = ordinary
                    },
                    programs,
                    pids
                };
            }
        }

        private void Reset()
        {
            _pids.Clear(); _sections.Clear(); _programs.Clear(); _pmtPids.Clear(); _services.Clear(); _esTypes.Clear(); _esExtra.Clear();
            _length = 0; _synced = false;
            _packets = _nulls = _teiErrors = _syncLosses = _ccErrors = _windowTotal = _windowNulls = 0;
            _totalKbps = _nullKbps = 0; _windowStartMs = _clock.ElapsedMilliseconds;
        }

        private void Rates(long now)
        {
            double seconds = Math.Max(0.001, (now - _windowStartMs) / 1000.0);
            foreach (PidInfo info in _pids.Values) { info.Kbps = info.WindowPackets * Packet * 8 / seconds / 1000.0; info.WindowPackets = 0; }
            _totalKbps = _windowTotal * Packet * 8 / seconds / 1000.0;
            _nullKbps = _windowNulls * Packet * 8 / seconds / 1000.0;
            _windowTotal = _windowNulls = 0;
            _windowStartMs = now;
        }

        private void Process()
        {
            int pos = 0;
            while (true)
            {
                if (!_synced)
                {
                    int found = -1;
                    for (int i = pos; i + Packet < _length; i++)
                        if (_buffer[i] == 0x47 && _buffer[i + Packet] == 0x47) { found = i; break; }
                    if (found < 0)
                    {
                        // keep only the tail, in case a packet starts there
                        int keep = Math.Min(_length - pos, Packet);
                        Buffer.BlockCopy(_buffer, _length - keep, _buffer, 0, keep);
                        _length = keep;
                        return;
                    }
                    pos = found; _synced = true;
                }
                if (_length - pos < Packet) break;
                if (_buffer[pos] != 0x47) { _syncLosses++; _synced = false; pos++; continue; }
                OnPacket(pos);
                pos += Packet;
            }
            if (pos > 0) { Buffer.BlockCopy(_buffer, pos, _buffer, 0, _length - pos); _length -= pos; }
        }

        private PidInfo Pid(int pid)
        {
            PidInfo info;
            if (!_pids.TryGetValue(pid, out info)) _pids[pid] = info = new PidInfo();
            return info;
        }

        private void OnPacket(int p)
        {
            byte[] b = _buffer;
            int b1 = b[p + 1], b2 = b[p + 2], b3 = b[p + 3];
            bool tei = (b1 & 0x80) != 0, pusi = (b1 & 0x40) != 0;
            int pid = ((b1 & 0x1F) << 8) | b2, afc = (b3 >> 4) & 3, cc = b3 & 0x0F;
            _packets++; _windowTotal++;
            PidInfo info = Pid(pid);
            info.Packets++; info.WindowPackets++;
            if (pid == 0x1FFF) { _nulls++; _windowNulls++; return; }
            if (tei) { _teiErrors++; info.TeiErrors++; return; }

            bool hasPayload = (afc & 1) != 0;
            int start = p + 4;
            if ((afc & 2) != 0)
            {
                int length = b[p + 4];
                if (length > 0 && length <= 183 && (b[p + 5] & 0x10) != 0) info.HasPcr = true;
                start = p + 5 + length;
            }
            if (hasPayload)
            {
                if (info.LastCc >= 0)
                {
                    if (cc == info.LastCc)
                    {
                        if (info.DuplicateSeen) { _ccErrors++; info.CcErrors++; }   // one repeat is allowed, two are not
                        info.DuplicateSeen = true;
                    }
                    else
                    {
                        if (cc != ((info.LastCc + 1) & 0x0F)) { _ccErrors++; info.CcErrors++; }
                        info.DuplicateSeen = false;
                    }
                }
                info.LastCc = cc;
            }
            int end = p + Packet;
            if (hasPayload && start < end && (pid == 0 || pid == 0x11 || _pmtPids.ContainsKey(pid))) Psi(pid, pusi, b, start, end);
            else if (hasPayload && start < end && !info.InfoDone)
            {
                int type;
                if (_esTypes.TryGetValue(pid, out type)) Es(info, pid, type, pusi, b, start, end);
            }
        }

        // ---- PSI sections (PAT, PMT, SDT) ----
        private void Psi(int pid, bool pusi, byte[] b, int offset, int end)
        {
            Section section;
            if (!_sections.TryGetValue(pid, out section)) _sections[pid] = section = new Section();
            if (pusi)
            {
                int pointer = b[offset], at = offset + 1;
                if (section.Length > 0 && pointer > 0) { Append(section, b, at, Math.Min(pointer, end - at)); Complete(pid, section); }
                section.Length = 0;
                at += pointer;
                if (at < end) { Append(section, b, at, end - at); Complete(pid, section); }
            }
            else if (section.Length > 0)
            {
                Append(section, b, offset, end - offset);
                Complete(pid, section);
            }
        }

        private static void Append(Section section, byte[] b, int offset, int count)
        {
            if (count <= 0 || section.Length + count > section.Data.Length) { section.Length = 0; return; }
            Buffer.BlockCopy(b, offset, section.Data, section.Length, count);
            section.Length += count;
        }

        private void Complete(int pid, Section section)
        {
            while (section.Length >= 3)
            {
                byte[] d = section.Data;
                if (d[0] == 0xFF) { section.Length = 0; return; }   // stuffing
                int total = (((d[1] & 0x0F) << 8) | d[2]) + 3;
                if (total > d.Length) { section.Length = 0; return; }
                if (section.Length < total) return;
                Handle(d, total);
                int rest = section.Length - total;
                if (rest > 0) Buffer.BlockCopy(d, total, d, 0, rest);
                section.Length = rest;
            }
        }

        private void Handle(byte[] d, int total)
        {
            int table = d[0];
            if (table == 0x00 && total >= 12) Pat(d, total);
            else if (table == 0x02 && total >= 16) Pmt(d, total);
            else if (table == 0x42 && total >= 15) Sdt(d, total);
        }

        private void Pat(byte[] d, int total)
        {
            for (int i = 8; i + 4 <= total - 4; i += 4)
            {
                int program = (d[i] << 8) | d[i + 1], pid = ((d[i + 2] & 0x1F) << 8) | d[i + 3];
                if (program == 0) continue;   // the NIT pid
                _pmtPids[pid] = program;
                Program known;
                if (!_programs.TryGetValue(program, out known)) _programs[program] = known = new Program { Number = program };
                known.PmtPid = pid;
            }
        }

        private void Pmt(byte[] d, int total)
        {
            int number = (d[3] << 8) | d[4];
            Program program;
            if (!_programs.TryGetValue(number, out program)) _programs[number] = program = new Program { Number = number };
            program.PcrPid = ((d[8] & 0x1F) << 8) | d[9];
            int infoLength = ((d[10] & 0x0F) << 8) | d[11], i = 12 + infoLength;
            var streams = new List<EsInfo>();
            while (i + 5 <= total - 4)
            {
                var es = new EsInfo { Type = d[i], Pid = ((d[i + 1] & 0x1F) << 8) | d[i + 2] };
                int esInfo = ((d[i + 3] & 0x0F) << 8) | d[i + 4];
                int j = i + 5, stop = Math.Min(i + 5 + esInfo, total - 4);
                while (j + 2 <= stop)
                {
                    int tag = d[j], length = d[j + 1];
                    if (j + 2 + length > stop) break;
                    if (tag == 0x0A && length >= 3) es.Language = Encoding.ASCII.GetString(d, j + 2, 3);
                    else if (tag == 0x6A) es.Extra = "AC-3";
                    else if (tag == 0x7A) es.Extra = "E-AC-3";
                    else if (tag == 0x59) es.Extra = "Subtitles";
                    else if (tag == 0x56) es.Extra = "Teletext";
                    j += 2 + length;
                }
                streams.Add(es);
                i += 5 + esInfo;
            }
            program.Streams = streams;
            foreach (EsInfo es in streams) { _esTypes[es.Pid] = es.Type; _esExtra[es.Pid] = es.Extra; }
        }

        private void Sdt(byte[] d, int total)
        {
            int i = 11;
            while (i + 5 <= total - 4)
            {
                int service = (d[i] << 8) | d[i + 1];
                int loop = ((d[i + 3] & 0x0F) << 8) | d[i + 4], j = i + 5, stop = Math.Min(i + 5 + loop, total - 4);
                while (j + 2 <= stop)
                {
                    int tag = d[j], length = d[j + 1];
                    if (j + 2 + length > stop) break;
                    if (tag == 0x48 && length >= 3)
                    {
                        int type = d[j + 2], providerLength = d[j + 3], at = j + 4;
                        if (at + providerLength + 1 <= j + 2 + length)
                        {
                            string provider = Text(d, at, providerLength);
                            at += providerLength;
                            int nameLength = d[at++];
                            if (at + nameLength <= j + 2 + length) _services[service] = new[] { Text(d, at, nameLength), provider, ServiceType(type) };
                        }
                    }
                    j += 2 + length;
                }
                i += 5 + loop;
            }
        }


        // ---- elementary stream details ----
        private void Es(PidInfo info, int pid, int type, bool pusi, byte[] b, int start, int end)
        {
            switch (type)
            {
                case 0x01: case 0x02: case 0x1B: case 0x24:
                    ScanVideo(info, type, b, start, end);
                    break;
                case 0x03: case 0x04: case 0x0F: case 0x81:
                    if (pusi) Audio(info, type, b, start, end);
                    break;
                case 0x06:
                    string extra;
                    if (pusi && _esExtra.TryGetValue(pid, out extra) && extra == "AC-3") Audio(info, 0x81, b, start, end);
                    break;
            }
        }

        // Looks for the start codes 00 00 01 in the stream and copies the sequence header / SPS that follows it.
        private void ScanVideo(PidInfo info, int type, byte[] b, int start, int end)
        {
            if (info.Cap == null) info.Cap = new byte[256];
            for (int i = start; i < end; i++)
            {
                byte x = b[i];
                if (info.CapKind != 0)
                {
                    info.Cap[info.CapLen++] = x;
                    if (x == 0) info.ZeroRun++;
                    else if (x == 1 && info.ZeroRun >= 2) { info.CapLen -= 3; FinishVideo(info, type); info.ZeroRun = 0; info.NextIsCode = true; if (info.InfoDone) return; continue; }
                    else info.ZeroRun = 0;
                    if (info.CapLen >= info.Cap.Length) FinishVideo(info, type);
                    if (info.InfoDone) return;
                    continue;
                }
                if (info.NextIsCode)
                {
                    info.NextIsCode = false; info.ZeroRun = 0;
                    int kind = type == 0x1B && (x & 0x1F) == 7 ? 1 : type == 0x24 && ((x >> 1) & 0x3F) == 33 ? 2 : (type == 0x01 || type == 0x02) && x == 0xB3 ? 3 : 0;
                    if (kind != 0) { info.CapKind = kind; info.CapLen = 0; info.Cap[info.CapLen++] = x; }
                    continue;
                }
                if (x == 0) info.ZeroRun++;
                else { if (x == 1 && info.ZeroRun >= 2) info.NextIsCode = true; info.ZeroRun = 0; }
            }
        }

        private void FinishVideo(PidInfo info, int type)
        {
            int kind = info.CapKind, length = info.CapLen;
            info.CapKind = 0; info.CapLen = 0;
            try
            {
                string text = null;
                if (kind == 1 && length > 4) text = H264(Unescape(info.Cap, 1, length));
                else if (kind == 2 && length > 8) text = H265(Unescape(info.Cap, 2, length));
                else if (kind == 3 && length >= 5) text = Mpeg2Video(info.Cap);
                if (!string.IsNullOrEmpty(text)) { info.Info = text; info.InfoDone = true; }
            }
            catch (Exception) { }
        }

        // removes the emulation-prevention bytes (00 00 03 -> 00 00)
        private static byte[] Unescape(byte[] d, int from, int to)
        {
            var o = new byte[to - from]; int n = 0, zeros = 0;
            for (int i = from; i < to; i++)
            {
                if (zeros >= 2 && d[i] == 3) { zeros = 0; continue; }
                o[n++] = d[i];
                zeros = d[i] == 0 ? zeros + 1 : 0;
            }
            var result = new byte[n]; Buffer.BlockCopy(o, 0, result, 0, n); return result;
        }

        private static string Fps(double value) { return value >= 100 || value <= 0 ? "" : " · " + Math.Round(value, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " fps"; }

        private static string H264(byte[] sps)
        {
            var b = new Bits(sps, sps.Length);
            int profile = (int)b.Read(8); b.Read(8); int level = (int)b.Read(8); b.Ue();
            int chroma = 1;
            if (profile == 100 || profile == 110 || profile == 122 || profile == 244 || profile == 44 || profile == 83 || profile == 86 || profile == 118 || profile == 128 || profile == 138 || profile == 139 || profile == 134 || profile == 135)
            {
                chroma = (int)b.Ue(); if (chroma == 3) b.Read(1);
                b.Ue(); b.Ue(); b.Read(1);
                if (b.Read(1) == 1)
                {
                    int lists = chroma != 3 ? 8 : 12;
                    for (int i = 0; i < lists; i++)
                        if (b.Read(1) == 1)
                        {
                            int size = i < 6 ? 16 : 64, last = 8, next = 8;
                            for (int j = 0; j < size; j++)
                            {
                                if (next != 0) next = (last + b.Se() + 256) % 256;
                                if (next != 0) last = next;
                            }
                        }
                }
            }
            b.Ue();
            uint poc = b.Ue();
            if (poc == 0) b.Ue();
            else if (poc == 1) { b.Read(1); b.Se(); b.Se(); uint n = b.Ue(); for (uint i = 0; i < n && i < 256; i++) b.Se(); }
            b.Ue(); b.Read(1);
            int widthMbs = (int)b.Ue() + 1, heightMap = (int)b.Ue() + 1, frameMbsOnly = (int)b.Read(1);
            if (frameMbsOnly == 0) b.Read(1);
            b.Read(1);
            int cl = 0, cr = 0, ct = 0, cb = 0;
            if (b.Read(1) == 1) { cl = (int)b.Ue(); cr = (int)b.Ue(); ct = (int)b.Ue(); cb = (int)b.Ue(); }
            int unitX = (chroma == 1 || chroma == 2) ? 2 : 1, unitY = (chroma == 1 ? 2 : 1) * (2 - frameMbsOnly);
            int width = widthMbs * 16 - unitX * (cl + cr), height = (2 - frameMbsOnly) * heightMap * 16 - unitY * (ct + cb);
            double fps = 0;
            if (b.Read(1) == 1)   // VUI
            {
                if (b.Read(1) == 1 && b.Read(8) == 255) b.Read(32);
                if (b.Read(1) == 1) b.Read(1);
                if (b.Read(1) == 1) { b.Read(4); if (b.Read(1) == 1) b.Read(24); }
                if (b.Read(1) == 1) { b.Ue(); b.Ue(); }
                if (b.Read(1) == 1) { uint units = b.Read(32), scale = b.Read(32); if (units > 0) fps = scale / (2.0 * units); }
            }
            if (b.Overrun && width <= 0) return null;
            string name = profile == 66 ? "Baseline" : profile == 77 ? "Main" : profile == 88 ? "Extended" : profile == 100 ? "High" : profile == 110 ? "High 10" : profile == 122 ? "High 4:2:2" : profile == 244 ? "High 4:4:4" : "profile " + profile;
            string levelText = level == 9 ? "1b" : (level / 10) + "." + (level % 10);
            return "H.264 " + name + " @ L" + levelText + " · " + width + "×" + height + Fps(fps);
        }

        private static string H265(byte[] sps)
        {
            var b = new Bits(sps, sps.Length);
            b.Read(4); int maxSub = (int)b.Read(3); b.Read(1);
            b.Read(2); b.Read(1); int profile = (int)b.Read(5); b.Read(32); b.Read(4); b.Read(32); b.Read(12);
            int level = (int)b.Read(8);
            var profilePresent = new bool[8]; var levelPresent = new bool[8];
            for (int i = 0; i < maxSub; i++) { profilePresent[i] = b.Read(1) == 1; levelPresent[i] = b.Read(1) == 1; }
            if (maxSub > 0) for (int i = maxSub; i < 8; i++) b.Read(2);
            for (int i = 0; i < maxSub; i++) { if (profilePresent[i]) { b.Read(32); b.Read(32); b.Read(24); } if (levelPresent[i]) b.Read(8); }
            b.Ue();
            int chroma = (int)b.Ue(); if (chroma == 3) b.Read(1);
            int width = (int)b.Ue(), height = (int)b.Ue();
            if (b.Read(1) == 1)
            {
                int subW = (chroma == 1 || chroma == 2) ? 2 : 1, subH = chroma == 1 ? 2 : 1;
                int l = (int)b.Ue(), rr = (int)b.Ue(), t = (int)b.Ue(), bt = (int)b.Ue();
                width -= subW * (l + rr); height -= subH * (t + bt);
            }
            if (width <= 0 || height <= 0 || b.Overrun) return null;
            string name = profile == 1 ? "Main" : profile == 2 ? "Main 10" : profile == 3 ? "Main Still Picture" : profile == 4 ? "Range Extensions" : "profile " + profile;
            return "H.265 " + name + " @ L" + (level / 30) + "." + ((level % 30) / 3) + " · " + width + "×" + height;
        }

        private static string Mpeg2Video(byte[] c)
        {
            // c[0] = 0xB3, then 12 bits width, 12 bits height, 4 bits aspect, 4 bits frame rate
            if (c.Length < 6) return null;
            int width = (c[1] << 4) | (c[2] >> 4), height = ((c[2] & 0x0F) << 8) | c[3], aspect = c[4] >> 4, rate = c[4] & 0x0F;
            double[] rates = { 0, 24000 / 1001.0, 24, 25, 30000 / 1001.0, 30, 50, 60000 / 1001.0, 60 };
            string[] shapes = { "", "1:1", "4:3", "16:9", "2.21:1" };
            if (width <= 0 || height <= 0) return null;
            return "MPEG-2 video · " + width + "×" + height + (aspect >= 1 && aspect <= 4 ? " (" + shapes[aspect] + ")" : "") + (rate >= 1 && rate <= 8 ? Fps(rates[rate]) : "");
        }

        private static void Audio(PidInfo info, int type, byte[] b, int start, int end)
        {
            int p = start;
            if (end - p >= 9 && b[p] == 0 && b[p + 1] == 0 && b[p + 2] == 1) p += 9 + b[p + 8];   // skip the PES header
            string text = null;
            int limit = Math.Min(end - 6, p + 40);
            for (int i = p; i < limit && text == null; i++)
            {
                if (type == 0x03 || type == 0x04) text = MpegAudio(b, i);
                else if (type == 0x0F) text = Adts(b, i);
                else if (type == 0x81 && b[i] == 0x0B && b[i + 1] == 0x77)
                {
                    int fscod = b[i + 4] >> 6;
                    if (fscod < 3) text = "AC-3 · " + new[] { "48", "44.1", "32" }[fscod] + " kHz";
                }
            }
            if (text != null) { info.Info = text; info.InfoDone = true; }
        }

        private static string MpegAudio(byte[] d, int i)
        {
            if (d[i] != 0xFF || (d[i + 1] & 0xE0) != 0xE0) return null;
            int version = (d[i + 1] >> 3) & 3, layer = (d[i + 1] >> 1) & 3, bitrate = d[i + 2] >> 4, rate = (d[i + 2] >> 2) & 3, mode = d[i + 3] >> 6;
            if (version == 1 || layer == 0 || bitrate == 0 || bitrate == 15 || rate == 3) return null;
            int[] l1 = { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 };
            int[] l2 = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 };
            int[] l3 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
            int[] low = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };
            int[] table = version == 3 ? (layer == 3 ? l1 : layer == 2 ? l2 : l3) : (layer == 3 ? new[] { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 } : low);
            int[] rates = version == 3 ? new[] { 44100, 48000, 32000 } : version == 2 ? new[] { 22050, 24000, 16000 } : new[] { 11025, 12000, 8000 };
            string layerName = layer == 3 ? "I" : layer == 2 ? "II" : "III";
            return "MPEG" + (version == 3 ? "-1" : "-2") + " Layer " + layerName + " · " + (rates[rate] / 1000.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " kHz · " + (mode == 3 ? "mono" : "stereo") + " · " + table[bitrate] + " kbit/s";
        }

        private static string Adts(byte[] d, int i)
        {
            if (d[i] != 0xFF || (d[i + 1] & 0xF6) != 0xF0) return null;
            int profile = (d[i + 2] >> 6) & 3, index = (d[i + 2] >> 2) & 0x0F, channels = ((d[i + 2] & 1) << 2) | (d[i + 3] >> 6);
            int[] rates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };
            if (index >= rates.Length) return null;
            string name = profile == 0 ? "AAC Main" : profile == 1 ? "AAC-LC" : profile == 2 ? "AAC SSR" : "AAC LTP";
            return name + " · " + (rates[index] / 1000.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " kHz · " + (channels == 1 ? "mono" : channels == 2 ? "stereo" : channels + " channels");
        }

        private static string Text(byte[] d, int offset, int length)
        {
            if (length <= 0) return "";
            int skip = d[offset] < 0x20 ? 1 : 0;   // a leading code-table byte (0x15 = UTF-8)
            string text = d[offset] == 0x15 ? Encoding.UTF8.GetString(d, offset + 1, length - 1) : Encoding.GetEncoding(28591).GetString(d, offset + skip, length - skip);
            var sb = new StringBuilder();
            foreach (char c in text) if (c >= 0x20 && !(c >= 0x80 && c < 0xA0)) sb.Append(c);
            return sb.ToString().Trim();
        }

        private static string ServiceType(int type)
        {
            switch (type)
            {
                case 0x01: return "SD TV";
                case 0x02: return "Radio";
                case 0x0C: return "Data";
                case 0x11: return "MPEG-2 HD TV";
                case 0x16: return "H.264 SD TV";
                case 0x19: return "H.264 HD TV";
                case 0x1F: return "HEVC TV";
                default: return "Service type 0x" + type.ToString("X2");
            }
        }

        private static string WellKnown(int pid, PidInfo info)
        {
            switch (pid)
            {
                case 0x0000: return "PAT";
                case 0x0001: return "CAT";
                case 0x0010: return "NIT";
                case 0x0011: return "SDT / BAT";
                case 0x0012: return "EIT";
                case 0x0013: return "RST";
                case 0x0014: return "TDT / TOT";
                case 0x1FFF: return "Null (padding)";
                default: return info.HasPcr ? "PCR" : "Other";
            }
        }

        private static string StreamTypeName(int type)
        {
            switch (type)
            {
                case 0x01: return "Video MPEG-1";
                case 0x02: return "Video MPEG-2";
                case 0x03: return "Audio MPEG-1";
                case 0x04: return "Audio MPEG-2";
                case 0x06: return "Private data (PES)";
                case 0x0F: return "Audio AAC (ADTS)";
                case 0x11: return "Audio AAC (LATM)";
                case 0x1B: return "Video H.264";
                case 0x24: return "Video H.265";
                case 0x33: return "Video H.266";
                case 0x81: return "Audio AC-3";
                case 0x87: return "Audio E-AC-3";
                default: return "Stream type 0x" + type.ToString("X2");
            }
        }
    }
}
