using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WGClientWifiSwitcher
{
    public class TunnelRule : INotifyPropertyChanged
    {
        private string _ssid   = "";
        private string _tunnel = "";

        public string Ssid   { get => _ssid;   set { _ssid   = value; OnProp(); } }
        public string Tunnel { get => _tunnel; set { _tunnel = value; OnProp(); OnProp(nameof(TunnelDisplay)); } }

        [JsonIgnore] public string TunnelDisplay => string.IsNullOrEmpty(_tunnel) ? "\u2014 disconnect" : _tunnel;
        [JsonIgnore] public string StatusText { get; set; } = "\u2014";
        [JsonIgnore] public SolidColorBrush StatusColor =>
            StatusText == "\u25cf active"
                ? new SolidColorBrush(Color.FromRgb(63, 185, 80))
                : new SolidColorBrush(Color.FromRgb(139, 148, 158));

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnProp([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // Represents a known tunnel with live status for the manual panel
    public class TunnelEntry : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _active = false;

        public string Name   { get; set; } = "";

        public bool Active
        {
            get => _active;
            set { _active = value; OnProp(); OnProp(nameof(StatusText)); OnProp(nameof(StatusColor)); OnProp(nameof(ButtonLabel)); }
        }

        public string StatusText  => _active ? "● Connected" : "○ Disconnected";
        public System.Windows.Media.SolidColorBrush StatusColor =>
            _active
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 185, 80))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158));
        public string ButtonLabel => _active ? "Disconnect" : "Connect";

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnProp([System.Runtime.CompilerServices.CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
    }

    public class AppConfig
    {
        public List<TunnelRule> Rules         { get; set; } = new();
        public string           DefaultAction { get; set; } = "none";
        public string           DefaultTunnel { get; set; } = "";
        public string           ConfDirectory { get; set; } = @"C:\Program Files\WireGuard\Data\Configurations";
    }

    public partial class MainWindow : Window
    {
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WGClientWifiSwitcher", "config.json");

        private static AppConfig _cfg = new();
        private readonly ObservableCollection<TunnelRule>  _rules   = new();
        private readonly ObservableCollection<TunnelEntry> _tunnels = new();
        private string? _lastWifi;
        private readonly DispatcherTimer _timer = new();
        private bool _loading = false;

        // WireGuard executable
        private static readonly string WgExe =
            File.Exists(@"C:\Program Files\WireGuard\wireguard.exe")
                ? @"C:\Program Files\WireGuard\wireguard.exe"
                : "wireguard";

        // Search known locations for a tunnel's .conf file
        private static string? FindConfPath(string tunnel, out string searched)
        {
            var dirs = new List<string>();

            // User-configured directory first
            if (!string.IsNullOrWhiteSpace(_cfg.ConfDirectory))
                dirs.Add(_cfg.ConfDirectory);

            dirs.AddRange(new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),       "WireGuard"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),  "WireGuard"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WireGuard"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),          "WireGuard"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),          "WireGuard", "Data"),
                @"C:\WireGuard",
            });

            var tried = new System.Text.StringBuilder();
            foreach (var dir in dirs)
            {
                var p = Path.Combine(dir, tunnel + ".conf");
                if (File.Exists(p)) { searched = tried.ToString(); return p; }
                var pd = Path.Combine(dir, tunnel + ".conf.dpapi");
                if (File.Exists(pd)) { searched = tried.ToString(); return pd; }
            }
            searched = tried.ToString();
            return null;
        }

        private static string? FindConfPath(string tunnel) => FindConfPath(tunnel, out _);

        // Returns tunnel names from .conf files in the configured directory
        internal static List<string> GetAvailableTunnels()
        {
            // Strategy 1: scan .conf files from configured/known directories
            var fromFiles = GetTunnelsFromFiles();
            if (fromFiles.Count > 0) return fromFiles;

            // Strategy 2: read tunnel names from installed Windows services
            // WireGuard registers each tunnel as WireGuardTunnel$<name> — readable by admins
            var fromServices = GetTunnelsFromServices();
            if (fromServices.Count > 0) return fromServices;

            return new List<string>();
        }

        private static List<string> GetTunnelsFromFiles()
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(_cfg.ConfDirectory))
                candidates.Add(_cfg.ConfDirectory);
            candidates.AddRange(new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),       "WireGuard"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),  "WireGuard"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WireGuard"),
                @"C:\Program Files\WireGuard\Data\Configurations",
                @"C:\ProgramData\WireGuard",
            });

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    if (!Directory.Exists(candidate)) continue;
                    // WireGuard stores configs as .conf (portable) or .conf.dpapi (DPAPI-encrypted)
                    var found = Directory.GetFiles(candidate)
                        .Where(f => f.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(".conf.dpapi", StringComparison.OrdinalIgnoreCase))
                        .Select(f => {
                            var name = Path.GetFileName(f);
                            if (name.EndsWith(".conf.dpapi", StringComparison.OrdinalIgnoreCase))
                                name = name.Substring(0, name.Length - ".conf.dpapi".Length);
                            else
                                name = Path.GetFileNameWithoutExtension(name);
                            return name;
                        })
                        .OrderBy(n => n)
                        .ToList();
                    if (found.Count > 0)
                    {
                        if (_cfg.ConfDirectory != candidate)
                            _cfg.ConfDirectory = candidate;
                        return found;
                    }
                }
                catch { }
            }
            return new List<string>();
        }

        private static List<string> GetTunnelsFromServices()
        {
            // WireGuard registers tunnel services as "WireGuardTunnel$<TunnelName>"
            // These are visible in the SCM even without access to the conf files
            try
            {
                return ServiceController.GetServices()
                    .Where(s => s.ServiceName.StartsWith("WireGuardTunnel$", StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.ServiceName.Substring("WireGuardTunnel$".Length))
                    .OrderBy(n => n)
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        // ── Constructor ──────────────────────────────────────────────────────

        public MainWindow()
        {
            InitializeComponent();
            RulesListView.ItemsSource  = _rules;
            TunnelsListView.ItemsSource = _tunnels;

            System.Windows.Application.Current.DispatcherUnhandledException += (s, e) =>
            {
                Log("ERROR: " + e.Exception.Message, LogLevel.Warn);
                e.Handled = true;
            };

            Loaded += (_, _) =>
            {
                LoadConfig();
                UpdateAdminLabel();
                SetupTimer();
                Log("Application started.", LogLevel.Info);
            };
        }

        // ── Taskbar icon (force AppWindow style so it appears without WindowStyle) ──

        private const int GWL_EXSTYLE      = -20;
        private const int WS_EX_APPWINDOW  = 0x00040000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

        // ── Wlan native API ───────────────────────────────────────────────────
        [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);
        [DllImport("wlanapi.dll")] static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
        [DllImport("wlanapi.dll")] static extern uint WlanRegisterNotification(IntPtr clientHandle, uint dwNotifSource, bool bIgnoreDuplicate, WlanNotificationCallback funcCallback, IntPtr pCallbackContext, IntPtr pReserved, out uint pdwPrevNotifSource);

        private const uint WLAN_NOTIFICATION_SOURCE_ACM = 0x00000008;
        private const uint WLAN_NOTIFICATION_SOURCE_ALL = 0x0000FFFF;

        [StructLayout(LayoutKind.Sequential)]
        private struct WLAN_NOTIFICATION_DATA
        {
            public uint NotificationSource;
            public uint NotificationCode;
            public Guid InterfaceGuid;
            public uint dwDataSize;
            public IntPtr pData;
        }

        private delegate void WlanNotificationCallback(ref WLAN_NOTIFICATION_DATA data, IntPtr context);

        private IntPtr _wlanHandle = IntPtr.Zero;
        private WlanNotificationCallback? _wlanCallback; // keep reference to prevent GC

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd  = new WindowInteropHelper(this).Handle;

            // Force taskbar button to appear (WindowStyle=None hides it otherwise)
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, (style & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW);

            // Set window icon to match the tray icon
            try
            {
                using var drawingIcon = TrayIconHelper.CreateIcon(false);
                Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    drawingIcon.Handle,
                    System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            }
            catch { }
        }

        // ── Timer ────────────────────────────────────────────────────────────

        private void SetupTimer()
        {
            // Slow timer — only refreshes status display every 10s, does NOT drive WiFi detection
            _timer.Interval = TimeSpan.FromSeconds(10);
            _timer.Tick += (_, _) => UpdateStatusDisplay();
            _timer.Start();

            // Register for instant WiFi change notifications via WlanApi
            RegisterWifiEvents();

            // Set initial state
            var wifi = GetCurrentSsid();
            _lastWifi = wifi;
            UpdateStatusDisplay(wifi);
        }

        // Called by WlanApi callback when connection state changes
        private void OnWifiChanged()
        {
            var wifi = GetCurrentSsid();
            UpdateStatusDisplay(wifi);
            if (wifi == _lastWifi) return;
            _lastWifi = wifi;
            Log("WiFi changed to: " + (wifi ?? "disconnected"), LogLevel.Info);
            ApplyRules(wifi);
        }

        public void UpdateStatusDisplay(string? wifi = null)
        {
            wifi ??= GetCurrentSsid();
            WifiLabel.Text       = wifi ?? "Not connected";
            WifiLabel.Foreground = wifi != null
                ? (SolidColorBrush)FindResource("Text")
                : (SolidColorBrush)FindResource("Sub");

            foreach (var rule in _rules)
                rule.StatusText = (!string.IsNullOrEmpty(rule.Tunnel) && GetTunnelStatus(rule.Tunnel))
                    ? "\u25cf active" : "\u2014";

            var active = GetActiveTunnelNames();
            TunnelLabel.Text       = active.Count > 0 ? string.Join(", ", active) : "\u2014";
            TunnelLabel.Foreground = active.Count > 0
                ? (SolidColorBrush)FindResource("Green")
                : (SolidColorBrush)FindResource("Sub");

            ((App)System.Windows.Application.Current).UpdateTrayStatus(TunnelLabel.Text, active.Count > 0);
            RefreshTunnelEntryStatuses();
        }

        private void RegisterWifiEvents()
        {
            try
            {
                uint result = WlanOpenHandle(2, IntPtr.Zero, out _, out _wlanHandle);
                if (result != 0) { Log("WlanApi unavailable (code " + result + "), using timer fallback.", LogLevel.Warn); _wlanHandle = IntPtr.Zero; return; }

                // Keep delegate alive — GC would collect it otherwise and crash
                _wlanCallback = (ref WLAN_NOTIFICATION_DATA data, IntPtr ctx) =>
                {
                    // ACM codes 9 = connected, 10 = disconnected, 21 = roaming
                    if (data.NotificationSource == WLAN_NOTIFICATION_SOURCE_ACM &&
                        (data.NotificationCode == 9 || data.NotificationCode == 10 || data.NotificationCode == 21))
                    {
                        // Must marshal back to UI thread
                        Dispatcher.BeginInvoke(new Action(OnWifiChanged));
                    }
                };

                WlanRegisterNotification(_wlanHandle, WLAN_NOTIFICATION_SOURCE_ACM, true, _wlanCallback, IntPtr.Zero, IntPtr.Zero, out _);
                Log("WiFi event monitoring active.", LogLevel.Ok);
            }
            catch (Exception ex) { Log("WlanApi error: " + ex.Message + " — using timer fallback.", LogLevel.Warn); }
        }

        // ── Rule logic ───────────────────────────────────────────────────────

        private void ApplyRules(string? ssid)
        {
            TunnelRule? match = null;
            if (ssid != null)
                match = _cfg.Rules.FirstOrDefault(r =>
                    string.Equals(r.Ssid.Trim(), ssid.Trim(), StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                if (string.IsNullOrEmpty(match.Tunnel))
                {
                    Log("Rule matched: " + ssid + " -> disconnect all", LogLevel.Ok);
                    DisconnectAll();
                }
                else
                {
                    Log("Rule matched: " + ssid + " -> activate " + match.Tunnel, LogLevel.Ok);
                    SwitchTo(match.Tunnel);
                }
            }
            else
            {
                switch (_cfg.DefaultAction)
                {
                    case "disconnect":
                        Log("No rule for " + (ssid ?? "disconnected") + " - disconnecting all.", LogLevel.Info);
                        DisconnectAll();
                        break;
                    case "activate" when !string.IsNullOrEmpty(_cfg.DefaultTunnel):
                        Log("No rule for " + (ssid ?? "disconnected") + " - activating default: " + _cfg.DefaultTunnel, LogLevel.Info);
                        SwitchTo(_cfg.DefaultTunnel);
                        break;
                    default:
                        Log("No rule for " + (ssid ?? "disconnected") + " - doing nothing.", LogLevel.Info);
                        break;
                }
            }
        }

        private void DisconnectAll()
        {
            foreach (var name in GetActiveTunnelNames())
                Log("  Stopped " + name + ": " + (StopTunnel(name) ? "OK" : "failed"), LogLevel.Warn);
        }

        private void SwitchTo(string target)
        {
            foreach (var name in GetActiveTunnelNames().Where(n => n != target))
            {
                StopTunnel(name);
                Log("  Stopped " + name, LogLevel.Warn);
            }

            if (GetTunnelStatus(target))
            {
                Log("  " + target + " already active.", LogLevel.Info);
                return;
            }

            LastError = "";
            bool ok = StartTunnel(target);
            Log("  Started " + target + ": " + (ok ? "OK" : "FAILED"), ok ? LogLevel.Ok : LogLevel.Warn);
            if (!ok && !string.IsNullOrEmpty(LastError)) Log("  " + LastError, LogLevel.Warn);
            if (!ok && !string.IsNullOrEmpty(LastError)) Log("  sc.exe: " + LastError, LogLevel.Warn);
        }

        // ── WireGuard helpers ────────────────────────────────────────────────

        private static string SvcName(string t) => "WireGuardTunnel$" + t;

        internal static string LastError = "";

        // Ensure WireGuardManager (the WireGuard system service) is running.
        // This is installed by WireGuard independently of the GUI and manages all tunnels.
        private static void EnsureManagerRunning()
        {
            try
            {
                using var mgr = new ServiceController("WireGuardManager");
                if (mgr.Status == ServiceControllerStatus.Stopped ||
                    mgr.Status == ServiceControllerStatus.Paused)
                {
                    mgr.Start();
                    mgr.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(8));
                }
            }
            catch { /* manager may not exist on all installs — tunnel service handles it */ }
        }

        private static bool StartTunnel(string tunnel)
        {
            // Make sure the WireGuard manager service is up first
            EnsureManagerRunning();

            // Primary: ServiceController — works without GUI, requires only the system service
            try
            {
                using var svc = new ServiceController(SvcName(tunnel));
                if (svc.Status == ServiceControllerStatus.Running) return true;
                svc.Start();
                svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                return true;
            }
            catch (Exception ex1)
            {
                LastError = "ServiceController: " + ex1.Message;
            }

            // Fallback: wireguard.exe /installtunnelservice — registers and starts in one step
            // Works if the service was never registered (e.g. after a reinstall)
            string searched;
            var conf = FindConfPath(tunnel, out searched);
            if (conf != null)
            {
                string err2;
                if (RunProcess(WgExe, "/installtunnelservice \"" + conf + "\"", out err2))
                {
                    System.Threading.Thread.Sleep(1500);
                    return true;
                }
                LastError += "  wireguard.exe: " + err2;
            }
            else
            {
                LastError += "  No .conf found. Searched: " + searched;
            }

            return false;
        }

        private static bool StopTunnel(string tunnel)
        {
            // Primary: ServiceController
            try
            {
                using var svc = new ServiceController(SvcName(tunnel));
                if (svc.Status == ServiceControllerStatus.Stopped) return true;
                svc.Stop();
                svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                return true;
            }
            catch (Exception ex)
            {
                LastError = "ServiceController stop: " + ex.Message;
                return false;
            }
        }

        private static bool RunProcess(string exe, string args, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using var p = Process.Start(psi)!;
                output = p.StandardOutput.ReadToEnd().Trim() + p.StandardError.ReadToEnd().Trim();
                p.WaitForExit(15000);
                return p.ExitCode == 0;
            }
            catch (Exception ex) { output = ex.Message; return false; }
        }

        private static bool GetTunnelStatus(string tunnel)
        {
            try
            {
                using var svc = new ServiceController(SvcName(tunnel));
                return svc.Status == ServiceControllerStatus.Running;
            }
            catch { return false; }
        }

        private static List<string> GetActiveTunnelNames()
        {
            try
            {
                return ServiceController.GetServices()
                    .Where(s => s.ServiceName.StartsWith("WireGuardTunnel$", StringComparison.OrdinalIgnoreCase)
                             && s.Status == ServiceControllerStatus.Running)
                    .Select(s => s.ServiceName.Substring("WireGuardTunnel$".Length))
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        // ── WiFi detection ────────────────────────────────────────────────────

        private static string? GetCurrentSsid()
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
                    { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var proc = Process.Start(psi)!;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);
                foreach (var line in output.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith("SSID") && !t.Contains("BSSID"))
                    {
                        var idx = t.IndexOf(':');
                        if (idx >= 0)
                        {
                            var ssid = t.Substring(idx + 1).Trim();
                            if (!string.IsNullOrEmpty(ssid)) return ssid;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        // ── Config ────────────────────────────────────────────────────────────

        private void LoadConfig()
        {
            _loading = true;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    _cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), opts) ?? new AppConfig();
                }
            }
            catch (Exception ex)
            {
                Log("Config load error: " + ex.Message, LogLevel.Warn);
                _cfg = new AppConfig();
            }
            finally
            {
                _rules.Clear();
                foreach (var r in _cfg.Rules) _rules.Add(r);

                ActionNone.IsChecked     = false;
                ActionDiscon.IsChecked   = false;
                ActionActivate.IsChecked = false;
                switch (_cfg.DefaultAction)
                {
                    case "disconnect": ActionDiscon.IsChecked   = true; break;
                    case "activate":   ActionActivate.IsChecked = true; break;
                    default:           ActionNone.IsChecked     = true; break;
                }

                RefreshTunnelDropdowns();
                DefaultTunnelBox.Text = _cfg.DefaultTunnel;
                ConfDirBox.Text       = _cfg.ConfDirectory;  // update in case auto-detected
                _loading = false;
            }
        }

        private void SaveConfig()
        {
            try
            {
                _cfg.Rules = _rules.ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.WriteAllText(ConfigPath,
                    JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true }));
                Log("Config saved (" + _cfg.Rules.Count + " rule(s))", LogLevel.Ok);
            }
            catch (Exception ex)
            {
                Log("Save error: " + ex.Message, LogLevel.Warn);
                System.Windows.MessageBox.Show("Failed to save:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── UI events ──────────────────────────────────────────────────────────

        private void AddRule_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Views.RuleDialog(GetCurrentSsid(), tunnels: GetAvailableTunnels()) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _rules.Add(new TunnelRule { Ssid = dlg.ResultSsid, Tunnel = dlg.ResultTunnel });
                SaveConfig();
                Log("Rule added: " + dlg.ResultSsid + " -> " +
                    (string.IsNullOrEmpty(dlg.ResultTunnel) ? "disconnect" : dlg.ResultTunnel), LogLevel.Ok);
            }
        }

        private void EditRule_Click(object sender, RoutedEventArgs e)
        {
            if (RulesListView.SelectedItem is not TunnelRule rule) return;
            var dlg = new Views.RuleDialog(null, rule.Ssid, rule.Tunnel, GetAvailableTunnels()) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                rule.Ssid   = dlg.ResultSsid;
                rule.Tunnel = dlg.ResultTunnel;
                SaveConfig();
                Log("Rule updated: " + dlg.ResultSsid + " -> " +
                    (string.IsNullOrEmpty(dlg.ResultTunnel) ? "disconnect" : dlg.ResultTunnel), LogLevel.Info);
            }
        }

        private void DeleteRule_Click(object sender, RoutedEventArgs e)
        {
            if (RulesListView.SelectedItem is not TunnelRule rule) return;
            if (System.Windows.MessageBox.Show("Delete rule for " + rule.Ssid + "?", "Delete Rule",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _rules.Remove(rule);
                SaveConfig();
                Log("Rule deleted: " + rule.Ssid, LogLevel.Warn);
            }
        }

        private void RulesListView_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            bool sel          = RulesListView.SelectedItem != null;
            EditBtn.IsEnabled   = sel;
            DeleteBtn.IsEnabled = sel;
        }

        private void AuthorLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("https://github.com/masselink/WGClientWifiSwitcher")
                      { UseShellExecute = true }); }
            catch { }
        }

        private void DefaultAction_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if      (ActionNone.IsChecked     == true) _cfg.DefaultAction = "none";
            else if (ActionDiscon.IsChecked   == true) _cfg.DefaultAction = "disconnect";
            else                                       _cfg.DefaultAction = "activate";
            SaveConfig();
        }

        private void RefreshTunnelDropdowns()
        {
            var tunnels = GetAvailableTunnels();

            Log("Tunnel discovery: found " + tunnels.Count + " tunnel(s)" +
                (tunnels.Count > 0 ? ": " + string.Join(", ", tunnels) : "") +
                "  [conf dir: " + (_cfg.ConfDirectory ?? "none") + "]", LogLevel.Info);

            // Update DefaultTunnelBox ComboBox
            var prev = DefaultTunnelBox.Text;
            DefaultTunnelBox.Items.Clear();
            foreach (var t in tunnels) DefaultTunnelBox.Items.Add(t);
            DefaultTunnelBox.Text = prev;

            // Update ConfDirBox to reflect auto-detected path
            if (!string.IsNullOrWhiteSpace(_cfg.ConfDirectory))
                ConfDirBox.Text = _cfg.ConfDirectory;

            // Rebuild tunnel panel list
            var active = GetActiveTunnelNames();
            _tunnels.Clear();
            foreach (var t in tunnels)
                _tunnels.Add(new TunnelEntry { Name = t, Active = active.Contains(t) });

            // Rebuild tray menu
            ((App)System.Windows.Application.Current).RebuildTrayTunnelMenu(tunnels, active);
        }

        // Called from UpdateStatusDisplay to refresh live Active flags without rebuilding
        private void RefreshTunnelEntryStatuses()
        {
            var active = GetActiveTunnelNames();
            foreach (var e in _tunnels) e.Active = active.Contains(e.Name);
            ((App)System.Windows.Application.Current).RebuildTrayTunnelMenu(
                _tunnels.Select(t => t.Name).ToList(), active);
        }

        private void DefaultTunnelBox_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (DefaultTunnelBox.SelectedItem is string s)
            {
                _cfg.DefaultTunnel = s;
                SaveConfig();
            }
        }

        private void DefaultTunnelBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _cfg.DefaultTunnel = DefaultTunnelBox.Text.Trim();
            SaveConfig();
        }

        public void ManualStart(string tunnel)
        {
            Log("Manual connect: " + tunnel, LogLevel.Info);
            LastError = "";
            bool ok = StartTunnel(tunnel);
            Log("  " + tunnel + ": " + (ok ? "connected" : "FAILED"), ok ? LogLevel.Ok : LogLevel.Warn);
            if (!ok && !string.IsNullOrEmpty(LastError)) Log("  " + LastError, LogLevel.Warn);
        }

        public void ManualStop(string tunnel)
        {
            Log("Manual disconnect: " + tunnel, LogLevel.Info);
            bool ok = StopTunnel(tunnel);
            Log("  " + tunnel + ": " + (ok ? "disconnected" : "FAILED"), ok ? LogLevel.Ok : LogLevel.Warn);
        }

        private void TunnelToggle_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as System.Windows.Controls.Button)?.DataContext is not TunnelEntry entry) return;
            if (entry.Active) ManualStop(entry.Name);
            else              ManualStart(entry.Name);
            UpdateStatusDisplay();
        }

        private void ConfDirBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _cfg.ConfDirectory = ConfDirBox.Text.Trim();
            RefreshTunnelDropdowns();
            SaveConfig();
        }

        private void BrowseConfDir_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description        = "Select the folder containing your WireGuard .conf files",
                UseDescriptionForTitle = true,
                SelectedPath       = Directory.Exists(_cfg.ConfDirectory)
                                        ? _cfg.ConfDirectory
                                        : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _cfg.ConfDirectory = dlg.SelectedPath;
                ConfDirBox.Text    = dlg.SelectedPath;
                SaveConfig();
                RefreshTunnelDropdowns();
                Log("Config folder set to: " + dlg.SelectedPath, LogLevel.Ok);
            }
        }

        // ── Logging ────────────────────────────────────────────────────────────

        private enum LogLevel { Info, Ok, Warn }
        private static readonly SolidColorBrush LInfo = new(Color.FromRgb(88,  166, 255));
        private static readonly SolidColorBrush LOk   = new(Color.FromRgb(63,  185,  80));
        private static readonly SolidColorBrush LWarn = new(Color.FromRgb(247, 129, 102));
        private static readonly SolidColorBrush LTime = new(Color.FromRgb(48,   54,  61));

        private void Log(string message, LogLevel level)
        {
            try
            {
                var para = new Paragraph { Margin = new Thickness(0) };
                para.Inlines.Add(new Run(DateTime.Now.ToString("HH:mm:ss") + "  ") { Foreground = LTime });
                para.Inlines.Add(new Run(message) { Foreground = level switch { LogLevel.Ok => LOk, LogLevel.Warn => LWarn, _ => LInfo } });
                if (LogDocument.Blocks.Count > 300) LogDocument.Blocks.Remove(LogDocument.Blocks.LastBlock);
                if (LogDocument.Blocks.FirstBlock != null)
                    LogDocument.Blocks.InsertBefore(LogDocument.Blocks.FirstBlock, para);
                else
                    LogDocument.Blocks.Add(para);
            }
            catch { }
        }

        // ── Window chrome ──────────────────────────────────────────────────────

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e) => Hide();
        private void CloseBtn_Click(object sender, RoutedEventArgs e)    => Hide();
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_wlanHandle != IntPtr.Zero) { WlanCloseHandle(_wlanHandle, IntPtr.Zero); _wlanHandle = IntPtr.Zero; }
            base.OnClosed(e);
        }

        private void UpdateAdminLabel()
        {
            bool admin = new System.Security.Principal.WindowsPrincipal(
                System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            AdminLabel.Text       = admin ? "\u25cf ADMIN" : "\u26a0 NOT ADMIN";
            AdminLabel.Foreground = admin ? (SolidColorBrush)FindResource("Green") : (SolidColorBrush)FindResource("Red");
        }
    }
}
