using Serilog;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace QocastPlayer
{
    internal sealed class TrayController : IDisposable
    {
        private readonly Thread _thread;
        private System.Windows.Forms.Control _dispatcher;
        private NotifyIcon _icon;

        public TrayController(string url, Action stop)
        {
            using (var ready = new ManualResetEvent(false))
            {
                Exception failure = null;
                bool initialized = false;
                _thread = new Thread(() =>
                {
                    try
                    {
                        using (var dispatcher = new System.Windows.Forms.Control())
                        using (var menu = new ContextMenuStrip())
                        using (var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                        using (var tray = new NotifyIcon())
                        {
                            _dispatcher = dispatcher;
                            var handle = dispatcher.Handle;
                            Action open = () =>
                            {
                                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                                catch (Exception ex) { Log.Error(ex, "Could not open receiver interface"); }
                            };
                            menu.Items.Add("Open interface", null, (sender, args) => open());
                            menu.Items.Add(new ToolStripSeparator());
                            menu.Items.Add("Exit app", null, (sender, args) =>
                            {
                                tray.Visible = false;
                                stop();
                            });
                            tray.Icon = icon;
                            tray.Text = "HB9-MultiTiouner — right-click to open or exit";
                            tray.ContextMenuStrip = menu;
                            tray.DoubleClick += (sender, args) => open();
                            tray.Visible = true;
                            _icon = tray;
                            initialized = true;
                            ready.Set();
                            Application.Run();
                            tray.Visible = false;
                        }
                    }
                    catch (Exception ex) { if (!initialized) { failure = ex; ready.Set(); } else { Log.Error(ex, "Receiver tray stopped unexpectedly"); stop(); } }
                }) { IsBackground = true, Name = "Receiver system tray" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                ready.WaitOne();
                if (failure != null)
                    throw new InvalidOperationException("Could not create the receiver tray icon.", failure);
            }
        }

        public void Dispose()
        {
            if (_dispatcher != null && !_dispatcher.IsDisposed)
            {
                try { _dispatcher.BeginInvoke(new Action(() => { _icon.Visible = false; Application.ExitThread(); })); }
                catch (InvalidOperationException) { }
            }
            if (Thread.CurrentThread != _thread)
                _thread.Join(3000);
        }
    }
}
