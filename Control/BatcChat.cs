using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QocastPlayer.Control
{
    // BATC QO-100 wideband chat (https://eshail.batc.org.uk/wb/chat/), room "eshail-wb".
    // Socket.IO 5 over a WebSocket: "0{..}" open, "40" join, "2"/"3" ping/pong, "42[\"event\",{..}]" an event.
    // Self-contained: it starts at the first request of the chat panel, never touches the tuners, and every failure
    // stays inside this class (the chat just shows "not reachable" and retries).
    internal sealed class BatcChat : IDisposable
    {
        private static readonly Uri Address = new Uri("wss://eshail.batc.org.uk/wb/chat/socket.io/?EIO=4&transport=websocket&room=eshail-wb");
        private const int Keep = 300, ReconnectMs = 5000, SilentSeconds = 40, MaxPacket = 4 << 20;

        private sealed class Message { public long N; public string Time, Name, Text; }

        private readonly object _sync = new object();
        private readonly List<Message> _messages = new List<Message>();
        private List<string> _nicks = new List<string>();
        private long _count, _generation, _viewers = -1;
        private string _error = "";
        private volatile bool _connected;
        private volatile bool _quit;
        private bool _started;
        private ClientWebSocket _socket;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private string _nickSent = "";

        public void Start()
        {
            lock (_sync)
            {
                if (_started || _quit) return;
                _started = true;
            }
            Task.Run(() => RunAsync());
        }

        public void Dispose()
        {
            _quit = true;
            try { _cancel.Cancel(); } catch { }
            try { _socket?.Abort(); } catch { }
        }

        public object Snapshot(long since, long generation)
        {
            lock (_sync)
            {
                bool reset = generation != _generation;
                return new
                {
                    connected = _connected,
                    viewers = _viewers < 0 ? (long?)null : _viewers,
                    error = _error,
                    generation = _generation,
                    reset,
                    nicks = _nicks.ToArray(),
                    messages = _messages.Where(m => reset || m.N > since)
                        .Select(m => new { n = m.N, time = m.Time, name = m.Name, message = m.Text }).ToArray()
                };
            }
        }

        // False (with the reason) if the message cannot go: no identity, nothing to say, not connected.
        public bool Send(string callsign, string name, string text, out string error)
        {
            string call = Clean(callsign, 20).ToUpperInvariant(), who = Clean(name, 30);
            string nick = call.Length == 0 ? who : who.Length == 0 ? call : call + " " + who;
            string words = Clean(text, 500);
            error = null;
            if (nick.Length == 0) { error = "Enter your callsign and name first"; return false; }
            if (words.Length == 0) { error = "Nothing to send"; return false; }
            ClientWebSocket socket = _socket;
            if (!_connected || socket == null || socket.State != WebSocketState.Open) { error = "Not connected to the BATC chat"; return false; }
            try
            {
                if (!_sendLock.Wait(3000)) { error = "The chat is busy, try again in a moment"; return false; }
                try
                {
                    if (nick != _nickSent)
                    {
                        WriteAsync(socket, "42" + new JArray("setnick", new JObject { ["nick"] = nick }).ToString(Newtonsoft.Json.Formatting.None)).Wait(5000);
                        _nickSent = nick;
                    }
                    WriteAsync(socket, "42" + new JArray("message", new JObject { ["message"] = words }).ToString(Newtonsoft.Json.Formatting.None)).Wait(5000);
                }
                finally { _sendLock.Release(); }
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "BATC chat send failed");
                error = "The message could not be sent";
                return false;
            }
        }

        // No control characters, no leading/trailing blanks, at most `limit` characters.
        private static string Clean(string input, int limit)
        {
            if (input == null) return "";
            var sb = new StringBuilder();
            foreach (char c in input)
            {
                char x = (c == '\r' || c == '\n' || c == '\t') ? ' ' : c;
                if (x < 32 || x == 127) continue;
                sb.Append(x);
            }
            string s = sb.ToString().Trim();
            return s.Length > limit ? s.Substring(0, limit) : s;
        }

        private async Task RunAsync()
        {
            while (!_quit)
            {
                bool reached = false;
                try { reached = await ConnectAndReadAsync(); }
                catch (Exception ex) { Log.Debug(ex, "BATC chat connection ended"); }
                _connected = false;
                lock (_sync) { if (!reached) _error = "BATC chat not reachable"; }
                try { await Task.Delay(ReconnectMs, _cancel.Token); } catch { }
            }
        }

        private async Task<bool> ConnectAndReadAsync()
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", "https://eshail.batc.org.uk");
            socket.Options.KeepAliveInterval = TimeSpan.Zero;   // Socket.IO pings by itself
            _socket = socket;
            _nickSent = "";
            long lastData = Environment.TickCount;
            bool reached = false;
            try
            {
                using (var connectLimit = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token))
                {
                    connectLimit.CancelAfter(8000);
                    await socket.ConnectAsync(Address, connectLimit.Token);
                }
                reached = true;
                lock (_sync) { _error = ""; }

                // A silent connection (not even the server's ping for 40 s) is dead: abort it so the read returns.
                var watchdog = Task.Run(async () =>
                {
                    while (!_quit && socket.State == WebSocketState.Open)
                    {
                        try { await Task.Delay(2000, _cancel.Token); } catch { return; }
                        if (Environment.TickCount - lastData > SilentSeconds * 1000) { try { socket.Abort(); } catch { } return; }
                    }
                });

                var buffer = new byte[16384];
                var packet = new StringBuilder();
                while (!_quit && socket.State == WebSocketState.Open)
                {
                    packet.Clear();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancel.Token);
                        lastData = Environment.TickCount;
                        if (result.MessageType == WebSocketMessageType.Close) return reached;
                        if (result.MessageType == WebSocketMessageType.Text) packet.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        if (packet.Length > MaxPacket) return reached;
                    } while (!result.EndOfMessage);
                    if (packet.Length > 0) await HandlePacketAsync(socket, packet.ToString());
                }
            }
            finally
            {
                _connected = false;
                try { socket.Abort(); } catch { }
                socket.Dispose();
                if (ReferenceEquals(_socket, socket)) _socket = null;
            }
            return reached;
        }

        private async Task HandlePacketAsync(ClientWebSocket socket, string text)
        {
            if (text == "2") { await GuardedWriteAsync(socket, "3"); return; }
            if (text[0] == '0') { await GuardedWriteAsync(socket, "40"); return; }   // join the default namespace
            if (text.StartsWith("40", StringComparison.Ordinal)) { _connected = true; return; }
            if (!text.StartsWith("42", StringComparison.Ordinal)) return;
            JArray list;
            try { using (var reader = new JsonTextReader(new StringReader(text.Substring(2))) { DateParseHandling = DateParseHandling.None }) list = JArray.Load(reader); } catch { return; }   // times stay strings
            if (list.Count == 0 || list[0].Type != JTokenType.String) return;
            string name = (string)list[0];
            JObject data = list.Count > 1 ? list[1] as JObject : null;
            if (data == null) return;
            lock (_sync)
            {
                if (name == "history")
                {
                    _messages.Clear();   // sent at every connect: the page starts again from it
                    var items = data["history"] as JArray;
                    if (items != null) foreach (JObject m in items.OfType<JObject>()) Add(Str(m, "time"), Str(m, "name"), Str(m, "message"));
                    ReadNicks(data);
                    _generation++;
                }
                else if (name == "message") Add(Str(data, "time"), Str(data, "name"), Str(data, "message"));
                else if (name == "nicks") ReadNicks(data);
                else if (name == "viewers") { JToken n = data["num"]; _viewers = n != null && (n.Type == JTokenType.Integer || n.Type == JTokenType.Float) ? (long)n : -1; }
            }
        }

        private static string Str(JObject o, string key) { JToken t = o[key]; return t != null && t.Type == JTokenType.String ? (string)t : ""; }

        private void ReadNicks(JObject data)
        {
            var list = data["nicks"] as JArray;
            if (list != null) _nicks = list.Where(n => n.Type == JTokenType.String).Select(n => (string)n).ToList();
        }

        private void Add(string time, string name, string text)   // _sync must be held
        {
            if (name.Length == 0 && text.Length == 0) return;
            _messages.Add(new Message { N = ++_count, Time = time, Name = name, Text = text });
            while (_messages.Count > Keep) _messages.RemoveAt(0);
        }

        private async Task GuardedWriteAsync(ClientWebSocket socket, string payload)
        {
            await _sendLock.WaitAsync(_cancel.Token);
            try { await WriteAsync(socket, payload); }
            finally { _sendLock.Release(); }
        }

        private Task WriteAsync(ClientWebSocket socket, string payload)
        {
            return socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload)), WebSocketMessageType.Text, true, _cancel.Token);
        }
    }
}
