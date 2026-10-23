using System;
using System.Text.RegularExpressions;

namespace QocastPlayer.Video
{
    internal sealed class VideoDiagnostics
    {
        private readonly object _sync = new object();
        private string _codec;
        private string _problem;

        public void Observe(string line)
        {
            lock (_sync)
            {
                // Output streams are H.264; only identify the original input codec.
                var match = Regex.Match(line, @"Stream #0:\d+.*?: Video: ([a-zA-Z0-9_]+)");
                if (match.Success && _codec == null) _codec = match.Groups[1].Value;
                var missing = Regex.Match(line, @"no decoder found for:?\s*([a-zA-Z0-9_]+)", RegexOptions.IgnoreCase);
                bool missingVideo = missing.Success && (_codec == missing.Groups[1].Value
                    || (_codec == null && Regex.IsMatch(missing.Groups[1].Value, @"^(vvc|hevc|h264|av1|mpeg2video|mpeg4|vp8|vp9)$")));
                if (missingVideo)
                {
                    _codec = missing.Groups[1].Value;
                    _problem = "unsupported";
                }
                else if (_codec != null && line.IndexOf(_codec, StringComparison.OrdinalIgnoreCase) >= 0
                    && Regex.IsMatch(line, @"Decoder .* not found|Unsupported codec|Decoder not found", RegexOptions.IgnoreCase))
                    _problem = "unsupported";
                else if (_problem != "unsupported" && Regex.IsMatch(line,
                    @"Error while decoding|Error submitting packet to decoder|Error opening input|Error initializing.*encoder|Error while opening encoder|Error opening output|Could not find codec parameters", RegexOptions.IgnoreCase))
                    _problem = "decode-error";
            }
        }

        public object Snapshot()
        {
            lock (_sync)
                return new { codec = _codec, problem = _problem };
        }
    }
}
