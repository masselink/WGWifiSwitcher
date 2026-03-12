using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace WGWifiSwitcher
{
    public partial class App : System.Windows.Application
    {
        private WinForms.NotifyIcon? _trayIcon;
        private MainWindow? _mainWindow;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (!IsAdmin())
            {
                var result = System.Windows.MessageBox.Show(
                    "WireGuard WiFi Switcher needs Administrator privileges to\n" +
                    "start and stop WireGuard tunnels.\n\nRelaunch as Administrator now?",
                    "Administrator Required",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    var psi = new ProcessStartInfo(
                        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName)
                    {
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    try { Process.Start(psi); } catch { }
                }
                Shutdown();
                return;
            }

            _mainWindow = new MainWindow();
            _mainWindow.Show();
            SetupTrayIcon();
        }

        private void SetupTrayIcon()
        {
            _trayIcon = new WinForms.NotifyIcon
            {
                Text = "WireGuard WiFi Switcher",
                Visible = true,
                Icon = TrayIconHelper.CreateIcon()
            };

            var menu = new WinForms.ContextMenuStrip();
            menu.BackColor = System.Drawing.Color.FromArgb(22, 27, 34);
            menu.ForeColor = System.Drawing.Color.FromArgb(230, 237, 243);

            var showItem = new WinForms.ToolStripMenuItem("Show Window");
            showItem.Font = new System.Drawing.Font("Consolas", 9f, System.Drawing.FontStyle.Bold);
            showItem.Click += (_, _) => ShowMainWindow();
            menu.Items.Add(showItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());

            var exitItem = new WinForms.ToolStripMenuItem("Exit");
            exitItem.Font = new System.Drawing.Font("Consolas", 9f);
            exitItem.Click += (_, _) => { _trayIcon!.Visible = false; Shutdown(); };
            menu.Items.Add(exitItem);

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
        }

        private void ShowMainWindow()
        {
            if (_mainWindow == null) return;
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }

        public void UpdateTrayStatus(string tunnelName, bool active)
        {
            if (_trayIcon == null) return;
            _trayIcon.Text = active
                ? $"WG WiFi Switcher \u2014 {tunnelName} active"
                : "WG WiFi Switcher \u2014 No tunnel active";
            _trayIcon.Icon = TrayIconHelper.CreateIcon(active);
        }

        private static bool IsAdmin() =>
            new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator);

        protected override void OnExit(ExitEventArgs e)
        {
            _trayIcon?.Dispose();
            base.OnExit(e);
        }
    }

    internal static class TrayIconHelper
    {
        public static System.Drawing.Icon CreateIcon(bool active = false)
        {
            using var bmp = new System.Drawing.Bitmap(16, 16);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.Clear(System.Drawing.Color.Transparent);

            var shieldColor = active
                ? System.Drawing.Color.FromArgb(63, 185, 80)
                : System.Drawing.Color.FromArgb(88, 166, 255);

            using var brush = new System.Drawing.SolidBrush(shieldColor);
            var pts = new System.Drawing.Point[]
            {
                new(8, 1), new(14, 4), new(14, 9),
                new(8, 15), new(2, 9), new(2, 4)
            };
            g.FillPolygon(brush, pts);

            using var wBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(14, 17, 23));
            g.FillRectangle(wBrush, 5, 7, 6, 5);

            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(14, 17, 23), 1.5f);
            g.DrawArc(pen, 5, 4, 5, 5, 180, 180);

            var hIcon = bmp.GetHicon();
            return System.Drawing.Icon.FromHandle(hIcon);
        }
    }
}
