using Newtonsoft.Json.Linq;
using QocastPlayer.Receiver;
using Serilog;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace QocastPlayer.Control
{
    // Asks GitHub whether a newer release exists, so that the page can say so.
    // Only the release list of this program's own repository is read; nothing about the user is sent.
    // Every failure (no internet, no repository yet, no release, a strange answer) is silent: the page just shows nothing.
    internal sealed class UpdateChecker : IDisposable
    {
        public const string Repository = "HB9IIU/HB9-MultiTiouner";
        public const string ReleaseListUrl = "https://api.github.com/repos/" + Repository + "/releases?per_page=15";
        public const string PagePrefix = "https://github.com/" + Repository + "/";

        private readonly string _url, _pagePrefix, _current;
        private readonly object _sync = new object();
        private Timer _timer;
        private bool _available;
        private string _version = "", _page = PagePrefix + "releases";
        private DateTime? _checkedAt;

        public UpdateChecker(string currentVersion, string url = ReleaseListUrl, string pagePrefix = PagePrefix)
        {
            _current = currentVersion ?? "";
            _url = url; _pagePrefix = pagePrefix;
        }

        // First check shortly after the start (not while the program is busy starting), then once a day.
        public void Start(int firstCheckAfterSeconds = 20)
        {
            lock (_sync)
            {
                if (_timer != null) return;
                _timer = new Timer(_ => CheckNow(), null, TimeSpan.FromSeconds(firstCheckAfterSeconds), TimeSpan.FromHours(24));
            }
        }

        // For the moment the user switches the check on.
        public void CheckSoon() { ThreadPool.QueueUserWorkItem(_ => CheckNow()); }

        public void CheckNow()
        {
            if (!AppSettings.CheckUpdates) return;
            try
            {
                string text = Download();
                if (text == null) return;
                Version best = null; string bestTag = null, bestPage = null;
                var releases = JArray.Parse(text);
                foreach (JObject release in releases.OfType<JObject>())
                {
                    if (release["draft"] != null && (bool)release["draft"]) continue;
                    string tag = (string)release["tag_name"];
                    Version version = Parse(tag);
                    if (version == null || (best != null && version <= best)) continue;
                    best = version; bestTag = tag; bestPage = (string)release["html_url"];
                }
                Version current = Parse(_current);
                lock (_sync)
                {
                    _checkedAt = DateTime.Now;
                    _available = best != null && current != null && best > current;
                    if (_available)
                    {
                        _version = bestTag.TrimStart('v', 'V');
                        // only a page of this repository is ever shown as the link
                        _page = bestPage != null && bestPage.StartsWith(_pagePrefix, StringComparison.Ordinal) ? bestPage : _pagePrefix + "releases";
                    }
                }
                if (_available) Log.Information("A newer version is available: {Version} (this is {Current})", bestTag, _current);
            }
            catch (Exception ex) { Log.Debug(ex, "The update check did not work (this is normal without internet or a release)"); }
        }

        public object Snapshot()
        {
            lock (_sync)
                return new { available = _available, version = _version, url = _page, current = _current, checkedAt = _checkedAt.HasValue ? _checkedAt.Value.ToString("s") : null };
        }

        public void Dispose()
        {
            lock (_sync) { if (_timer != null) { _timer.Dispose(); _timer = null; } }
        }

        // "v0.2.1" -> 0.2.1; anything after a "-" or "+" is ignored; null if it is not a version.
        internal static Version Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim().TrimStart('v', 'V');
            int cut = text.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) text = text.Substring(0, cut);
            string[] parts = text.Split('.');
            if (parts.Length < 2 || parts.Length > 4) return null;
            Version version;
            return Version.TryParse(text, out version) ? version : null;
        }

        private string Download()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            var request = (HttpWebRequest)WebRequest.Create(_url);
            request.UserAgent = "HB9-MultiTiouner/" + _current;
            request.Accept = "application/vnd.github+json";
            request.Timeout = 8000; request.ReadWriteTimeout = 8000;
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    string text = reader.ReadToEnd();
                    return text.Length > (1 << 20) ? null : text;   // a release list is a few kB; more than 1 MB is not one
                }
            }
            catch (WebException ex) when (ex.Response != null) { return null; }   // 404: no repository or no release yet
        }
    }
}
