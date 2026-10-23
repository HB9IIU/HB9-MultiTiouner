using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace QocastPlayer
{
    // Use the launching terminal when available; desktop launches stay windowless.
    internal sealed class ConsoleLog : ILogEventSink
    {
        private readonly object _sync = new object();
        private readonly MessageTemplateTextFormatter _formatter = new MessageTemplateTextFormatter(
            "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}", null);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint processId);

        public static bool Attach()
        {
            // ERROR_ACCESS_DENIED means this process already has a console.
            if (!AttachConsole(unchecked((uint)-1)) && Marshal.GetLastWin32Error() != 5)
                return false;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            return true;
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_sync)
            {
                // Closing a terminal must not interrupt reception or file logging.
                try { _formatter.Format(logEvent, Console.Out); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }
    }
}
