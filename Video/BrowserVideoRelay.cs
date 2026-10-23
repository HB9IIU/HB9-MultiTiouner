using QocastPlayer.Receiver;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace QocastPlayer.Video
{
    internal sealed class BrowserVideoRelay : IDisposable
    {
        public const int RtspPort = 18554;
        public const int WebRtcPort = 18889;
        public const int WebRtcUdpPort = 18190;
        private const int HttpPort = 47322;

        private readonly object _sync = new object();
        private readonly string _runtimeDirectory;
        private readonly Dictionary<int, Process> _publishers = new Dictionary<int, Process>();
        private readonly Dictionary<int, VideoDiagnostics> _diagnostics = new Dictionary<int, VideoDiagnostics>();
        private Process _mediaServer;
        private bool _disposed;

        public BrowserVideoRelay(string runtimeDirectory, int[] tunerNumbers)
        {
            _runtimeDirectory = runtimeDirectory;
            Directory.CreateDirectory(_runtimeDirectory);
            try
            {
                StartMediaServer(tunerNumbers);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void StartPublisher(int tunerNumber)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                StopPublisher(tunerNumber);
                string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe");
                if (!File.Exists(ffmpegPath))
                    throw new FileNotFoundException("The bundled FFmpeg executable is missing.", ffmpegPath);

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = BuildPublisherArguments(tunerNumber),
                    WorkingDirectory = _runtimeDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                VideoDiagnostics diagnostics;
                if (!_diagnostics.TryGetValue(tunerNumber, out diagnostics))
                    _diagnostics[tunerNumber] = diagnostics = new VideoDiagnostics();
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (!String.IsNullOrWhiteSpace(args.Data))
                    {
                        diagnostics.Observe(args.Data);
                        if (args.Data.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                            || args.Data.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
                            Log.Warning("Browser video tuner {Number}: {Output}", tunerNumber, args.Data);
                        else
                            Log.Debug("Browser video tuner {Number}: {Output}", tunerNumber, args.Data);
                    }
                };
                process.Exited += (sender, args) =>
                {
                    try
                    {
                        Log.Warning("Browser video encoder for tuner {Number} exited with code {ExitCode}",
                            tunerNumber, process.ExitCode);
                    }
                    catch (InvalidOperationException)
                    {
                        Log.Warning("Browser video encoder for tuner {Number} exited", tunerNumber);
                    }
                };

                if (!process.Start())
                {
                    process.Dispose();
                    throw new InvalidOperationException("Could not start the browser video encoder for tuner " + tunerNumber + ".");
                }

                process.BeginErrorReadLine();
                _publishers[tunerNumber] = process;
                Log.Information("Started browser video encoder for tuner {Number}", tunerNumber);
            }
        }

        public void RestartPublisher(int tunerNumber)
        {
            lock (_sync)
            {
                _diagnostics[tunerNumber] = new VideoDiagnostics();
                StartPublisher(tunerNumber);
            }
        }

        public void SuspendPublisher(int number)
        {
            lock (_sync) { StopPublisher(number); _diagnostics.Remove(number); }
        }

        public object GetDiagnostics(int tunerNumber)
        {
            lock (_sync)
            {
                VideoDiagnostics diagnostics;
                return _diagnostics.TryGetValue(tunerNumber, out diagnostics) ? diagnostics.Snapshot() : null;
            }
        }

        public void StartPublishers(IEnumerable<int> tunerNumbers)
        {
            var started = new List<int>();
            lock (_sync)
            {
                ThrowIfDisposed();
                try
                {
                    foreach (int tunerNumber in tunerNumbers)
                    {
                        StartPublisher(tunerNumber);
                        started.Add(tunerNumber);
                    }
                }
                catch
                {
                    foreach (int tunerNumber in started)
                        StopPublisher(tunerNumber);
                    throw;
                }
            }
        }

        public void EnsurePublishersRunning(IEnumerable<int> tunerNumbers)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                foreach (int tunerNumber in tunerNumbers)
                {
                    Process process;
                    if (!_publishers.TryGetValue(tunerNumber, out process) || process.HasExited)
                        StartPublisher(tunerNumber);
                }
            }
        }

        private void StartMediaServer(int[] tunerNumbers)
        {
            string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "mediamtx", "mediamtx.exe");
            if (!File.Exists(executable))
                throw new FileNotFoundException("The bundled MediaMTX executable is missing.", executable);

            string configPath = Path.Combine(_runtimeDirectory, "mediamtx.yml");
            var config = new StringBuilder()
                .AppendLine("logLevel: warn")
                .AppendLine("logDestinations: [stdout]")
                .AppendLine("rtsp: yes")
                .AppendLine("rtspAddress: 127.0.0.1:" + RtspPort)
                .AppendLine("rtspTransports: [tcp]")
                .AppendLine("rtmp: no")
                .AppendLine("hls: no")
                .AppendLine("srt: no")
                .AppendLine("webrtc: yes")
                .AppendLine("webrtcAddress: 127.0.0.1:" + WebRtcPort)
                .AppendLine("webrtcLocalUDPAddress: 127.0.0.1:" + WebRtcUdpPort)
                .AppendLine("webrtcLocalTCPAddress: ''")
                .AppendLine("webrtcIPsFromInterfaces: no")
                .AppendLine("webrtcAdditionalHosts: [127.0.0.1]")
                .AppendLine("authInternalUsers:")
                .AppendLine("- user: any")
                .AppendLine("  ips: [127.0.0.1]")
                .AppendLine("  permissions:")
                .AppendLine("  - action: publish")
                .AppendLine("  - action: read")
                .AppendLine("paths:");

            foreach (int tunerNumber in tunerNumbers)
            {
                config.AppendLine("  tuner" + tunerNumber + ":")
                    .AppendLine("    source: publisher");
            }

            File.WriteAllText(configPath, config.ToString(), new UTF8Encoding(false));

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = Quote(configPath),
                WorkingDirectory = _runtimeDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _mediaServer = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _mediaServer.OutputDataReceived += (sender, args) =>
            {
                if (!String.IsNullOrWhiteSpace(args.Data))
                    Log.Information("MediaMTX: {Output}", args.Data);
            };
            _mediaServer.ErrorDataReceived += (sender, args) =>
            {
                if (!String.IsNullOrWhiteSpace(args.Data))
                    Log.Warning("MediaMTX: {Output}", args.Data);
            };

            if (!_mediaServer.Start())
                throw new InvalidOperationException("Could not start the bundled MediaMTX WebRTC server.");
            _mediaServer.BeginOutputReadLine();
            _mediaServer.BeginErrorReadLine();
            WaitForRtspServer();
            Log.Information("MediaMTX WebRTC server is ready");
        }

        private void WaitForRtspServer()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (_mediaServer.HasExited)
                    throw new InvalidOperationException("MediaMTX exited during startup with code " + _mediaServer.ExitCode + ".");

                try
                {
                    using (var client = new TcpClient())
                    {
                        client.Connect("127.0.0.1", RtspPort);
                        return;
                    }
                }
                catch (SocketException)
                {
                    Thread.Sleep(100);
                }
            }

            throw new TimeoutException("MediaMTX did not open its local RTSP port within 10 seconds.");
        }

        private static string BuildPublisherArguments(int tunerNumber)
        {
            string source = "http://127.0.0.1:" + HttpPort + "/stream/" + tunerNumber;
            string destination = "rtsp://127.0.0.1:" + RtspPort + "/tuner" + tunerNumber;
            return "-hide_banner -nostdin -nostats -loglevel info "
                + "-fflags +nobuffer+discardcorrupt -flags -output_corrupt "
                + "-probesize 262144 -analyzeduration 1000000 "
                + "-i " + Quote(source) + " "
                + "-map 0:v:0 -map 0:a:0? "
                + "-vf " + Quote("scale=960:540:force_original_aspect_ratio=decrease:force_divisible_by=2") + " "
                + "-c:v h264_mf -b:v 1200k -pix_fmt yuv420p -bf 0 -g 30 "
                + "-c:a pcm_mulaw -ar 8000 -ac 1 "
                + "-flush_packets 1 -f rtsp -rtsp_transport tcp " + Quote(destination);
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private void StopPublisher(int tunerNumber)
        {
            Process process;
            if (!_publishers.TryGetValue(tunerNumber, out process))
                return;

            _publishers.Remove(tunerNumber);
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            catch (InvalidOperationException)
            {
                Log.Debug("Encoder process for tuner {Number} was already stopped", tunerNumber);
            }
            finally
            {
                process.Dispose();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BrowserVideoRelay));
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;

                foreach (int tunerNumber in new List<int>(_publishers.Keys))
                    StopPublisher(tunerNumber);

                if (_mediaServer != null)
                {
                    try
                    {
                        if (!_mediaServer.HasExited)
                        {
                            _mediaServer.Kill();
                            _mediaServer.WaitForExit(3000);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        Log.Debug("MediaMTX was already stopped");
                    }
                    finally
                    {
                        _mediaServer.Dispose();
                        _mediaServer = null;
                    }
                }
            }
        }
    }
}
