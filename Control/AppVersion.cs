using System;
using System.IO;
using System.Reflection;

namespace QocastPlayer
{
    // The version number (set by <Version> in the .csproj) and the time the running exe was built.
    internal static class AppVersion
    {
        public static readonly string Number = ReadNumber();
        public static readonly string Built = ReadBuilt();

        private static string ReadNumber()
        {
            try
            {
                var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                string text = info != null ? info.InformationalVersion : Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
                int plus = text.IndexOf('+');
                return plus >= 0 ? text.Substring(0, plus) : text;
            }
            catch { return ""; }
        }

        private static string ReadBuilt()
        {
            try { return File.GetLastWriteTime(Assembly.GetExecutingAssembly().Location).ToString("yyyy-MM-ddTHH:mm:ss"); }
            catch { return ""; }
        }
    }
}
