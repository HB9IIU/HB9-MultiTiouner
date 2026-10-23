using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace QocastPlayer.Control
{
    // "Stream out": a copy of each tuner's transport stream, sent as UDP multicast so that VLC, OBS or another PC
    // on the home network can open udp://@GROUP:PORT. Tuner N sends to port BasePort + N - 1.
    // Self-contained: Feed() is called from the tuner's read loop and never throws or blocks; with the switch off it does nothing.
    internal sealed class StreamOut : IDisposable
    {
        private const int TsPacket = 188, PacketsPerDatagram = 7;

        private sealed class Feeder
        {
            public byte[] Buffer = new byte[TsPacket * (PacketsPerDatagram + 24)];
            public int Length;
            public bool Synced;
        }

        private readonly string _path;
        private readonly object _sync = new object();
        private readonly Dictionary<int, Feeder> _feeders = new Dictionary<int, Feeder>();
        private Socket _socket;
        private volatile bool _enabled;
        private string _group = "239.255.0.1", _interfaceIp = "";
        private int _basePort = 1234;
        private DateTime _lastWarning = DateTime.MinValue;

        public StreamOut(string path)
        {
            _path = path;
            try
            {
                if (File.Exists(path))
                {
                    JObject saved = JObject.Parse(File.ReadAllText(path));
                    IPAddress parsed;
                    string group = (string)saved["group"];
                    if (group != null && IPAddress.TryParse(group, out parsed) && GroupError(group) == null) _group = group;
                    int port = saved["basePort"] != null ? (int)saved["basePort"] : 0;
                    if (port >= 1024 && port <= 65000) _basePort = port;
                    string iface = (string)saved["interfaceIp"] ?? "";
                    if (iface.Length == 0 || LocalAddresses().Any(a => a.Item1 == iface)) _interfaceIp = iface;
                    _enabled = saved["enabled"] != null && (bool)saved["enabled"];
                }
            }
            catch (Exception ex) { Log.Warning(ex, "Could not read the stream-out settings; using defaults (off)"); }
            if (_enabled) OpenSocket();
        }

        // Called with the bytes just read from tuner `number`; count == 0 means the stream starts again.
        public void Feed(int number, byte[] data, int count)
        {
            if (!_enabled) return;
            try
            {
                Socket socket = _socket;
                if (socket == null) return;
                Feeder feeder;
                lock (_sync)
                {
                    if (!_feeders.TryGetValue(number, out feeder)) _feeders[number] = feeder = new Feeder();
                }
                lock (feeder)
                {
                    if (count <= 0) { feeder.Length = 0; feeder.Synced = false; return; }
                    if (feeder.Length + count > feeder.Buffer.Length) Array.Resize(ref feeder.Buffer, feeder.Length + count + TsPacket * PacketsPerDatagram);
                    Buffer.BlockCopy(data, 0, feeder.Buffer, feeder.Length, count);
                    feeder.Length += count;

                    if (!feeder.Synced)
                    {
                        int start = -1;
                        for (int i = 0; i + TsPacket < feeder.Length; i++)
                            if (feeder.Buffer[i] == 0x47 && feeder.Buffer[i + TsPacket] == 0x47) { start = i; break; }
                        if (start < 0)
                        {
                            if (feeder.Length > TsPacket * 4) { Buffer.BlockCopy(feeder.Buffer, feeder.Length - TsPacket, feeder.Buffer, 0, TsPacket); feeder.Length = TsPacket; }
                            return;
                        }
                        if (start > 0) { Buffer.BlockCopy(feeder.Buffer, start, feeder.Buffer, 0, feeder.Length - start); feeder.Length -= start; }
                        feeder.Synced = true;
                    }

                    int datagram = TsPacket * PacketsPerDatagram, sent = 0;
                    var target = new IPEndPoint(IPAddress.Parse(_group), _basePort + number - 1);
                    while (feeder.Length - sent >= datagram)
                    {
                        socket.SendTo(feeder.Buffer, sent, datagram, SocketFlags.None, target);
                        sent += datagram;
                    }
                    if (sent > 0)
                    {
                        Buffer.BlockCopy(feeder.Buffer, sent, feeder.Buffer, 0, feeder.Length - sent);
                        feeder.Length -= sent;
                    }
                }
            }
            catch (Exception ex)
            {
                if (DateTime.UtcNow - _lastWarning > TimeSpan.FromSeconds(30))
                {
                    _lastWarning = DateTime.UtcNow;
                    Log.Warning(ex, "Stream out: sending failed");
                }
            }
        }

        public object Snapshot(IEnumerable<int> tunerNumbers)
        {
            lock (_sync)
            {
                IPAddress used = ChooseInterface();
                return new
                {
                    enabled = _enabled,
                    sending = _enabled && _socket != null,
                    group = _group,
                    basePort = _basePort,
                    interfaceIp = _interfaceIp,
                    interfaceUsed = used == null ? "" : used.ToString(),
                    interfaces = LocalAddresses().Select(a => new { ip = a.Item1, name = a.Item2 }).ToArray(),
                    tuners = tunerNumbers.Select(n => new
                    {
                        number = n,
                        port = _basePort + n - 1,
                        vlc = "udp://@" + _group + ":" + (_basePort + n - 1),
                        obs = "udp://" + _group + ":" + (_basePort + n - 1)
                    }).ToArray()
                };
            }
        }

        // Applies the fields present in `request` (enabled, group, basePort, interfaceIp); throws InvalidDataException on a bad value.
        public void Update(JObject request)
        {
            lock (_sync)
            {
                bool enabled = _enabled;
                string group = _group, iface = _interfaceIp;
                int port = _basePort;
                JToken token;
                if ((token = request["enabled"]) != null)
                {
                    if (token.Type != JTokenType.Boolean) throw new InvalidDataException("Stream out enabled must be true or false");
                    enabled = (bool)token;
                }
                if ((token = request["group"]) != null)
                {
                    group = ((string)token ?? "").Trim();
                    string problem = GroupError(group);
                    if (problem != null) throw new InvalidDataException(problem);
                }
                if ((token = request["basePort"]) != null)
                {
                    if (token.Type != JTokenType.Integer) throw new InvalidDataException("The first port must be a whole number");
                    port = (int)token;
                    if (port < 1024 || port > 65000) throw new InvalidDataException("The first port must be between 1024 and 65000");
                }
                if ((token = request["interfaceIp"]) != null)
                {
                    iface = ((string)token ?? "").Trim();
                    if (iface.Length > 0 && !LocalAddresses().Any(a => a.Item1 == iface))
                        throw new InvalidDataException("That network card is not available");
                }
                _enabled = false;   // stop feeding while the socket is replaced
                CloseSocket();
                _group = group; _basePort = port; _interfaceIp = iface;
                foreach (Feeder feeder in _feeders.Values) lock (feeder) { feeder.Length = 0; feeder.Synced = false; }
                if (enabled) OpenSocket();
                _enabled = enabled && _socket != null;
                try
                {
                    string temporary = _path + ".tmp";
                    File.WriteAllText(temporary, new JObject
                    {
                        ["enabled"] = enabled, ["group"] = _group, ["basePort"] = _basePort, ["interfaceIp"] = _interfaceIp
                    }.ToString());
                    if (File.Exists(_path)) File.Delete(_path);
                    File.Move(temporary, _path);
                }
                catch (Exception ex) { Log.Warning(ex, "Could not save the stream-out settings"); }
                Log.Information("Stream out {State}: {Group}, ports from {Port}, card {Card}", _enabled ? "on" : "off", _group, _basePort,
                    _interfaceIp.Length == 0 ? "automatic" : _interfaceIp);
            }
        }

        public void Dispose()
        {
            _enabled = false;
            lock (_sync) CloseSocket();
        }

        private static string GroupError(string group)
        {
            IPAddress address;
            if (!IPAddress.TryParse(group, out address) || address.AddressFamily != AddressFamily.InterNetwork || group.Split('.').Length != 4)
                return "The multicast address must look like 239.255.0.1";
            byte[] b = address.GetAddressBytes();
            if (b[0] < 224 || b[0] > 239) return "A multicast address starts with 224 to 239 (use 239.x.x.x at home)";
            if (b[0] == 224 && b[1] == 0 && b[2] == 0) return "224.0.0.x is reserved for the network itself; use 239.x.x.x";
            return null;
        }

        private void OpenSocket()
        {
            try
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                IPAddress iface = ChooseInterface();
                if (iface != null)
                {
                    socket.Bind(new IPEndPoint(iface, 0));
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, iface.GetAddressBytes());
                }
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);   // stays inside the home network
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);  // OBS/VLC on this PC receive it too
                socket.Blocking = false;
                _socket = socket;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Stream out: could not open the network socket");
                CloseSocket();
            }
        }

        private void CloseSocket()
        {
            Socket socket = _socket;
            _socket = null;
            try { socket?.Close(); } catch { }
        }

        // The card chosen by the user, else the one that has the default gateway (the home network).
        private IPAddress ChooseInterface()
        {
            IPAddress chosen;
            if (_interfaceIp.Length > 0 && IPAddress.TryParse(_interfaceIp, out chosen)) return chosen;
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!IsUsable(nic)) continue;
                    IPInterfaceProperties props = nic.GetIPProperties();
                    bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    if (!gateway) continue;
                    UnicastIPAddressInformation ip = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (ip != null) return ip.Address;
                }
            }
            catch (Exception ex) { Log.Debug(ex, "Stream out: no network card found"); }
            return null;
        }

        private static bool IsUsable(NetworkInterface nic)
        {
            return nic.OperationalStatus == OperationalStatus.Up
                && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                && nic.SupportsMulticast;
        }

        private static List<Tuple<string, string>> LocalAddresses()
        {
            var list = new List<Tuple<string, string>>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!IsUsable(nic)) continue;
                    foreach (UnicastIPAddressInformation ip in nic.GetIPProperties().UnicastAddresses)
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(Tuple.Create(ip.Address.ToString(), nic.Name));
                }
            }
            catch (Exception ex) { Log.Debug(ex, "Stream out: could not list the network cards"); }
            return list;
        }
    }
}
