using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
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

        public string StatusText  => _active ? Lang.T("TunnelStatusConnected") : Lang.T("TunnelStatusDisconnected");
        public System.Windows.Media.SolidColorBrush StatusColor =>
            _active
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 185, 80))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158));
        public string ButtonLabel => _active ? Lang.T("TunnelBtnDisconnect") : Lang.T("TunnelBtnConnect");

        public void RefreshLabels() { OnProp(nameof(StatusText)); OnProp(nameof(ButtonLabel)); }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnProp([System.Runtime.CompilerServices.CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
    }

    public class AppConfig
    {
        public List<TunnelRule> Rules            { get; set; } = new();
        public string           DefaultAction    { get; set; } = "none";
        public string           DefaultTunnel    { get; set; } = "";
        public string           InstallDirectory { get; set; } = @"C:\Program Files\WireGuard";
        public string           Language         { get; set; } = "en";

        [JsonIgnore]
        public string ConfDirectory => string.IsNullOrWhiteSpace(InstallDirectory)
            ? ""
            : Path.Combine(InstallDirectory, @"Data\Configurations");

        [JsonIgnore]
        public string WgExePath => string.IsNullOrWhiteSpace(InstallDirectory)
            ? "wireguard"
            : Path.Combine(InstallDirectory, "wireguard.exe");

        // ── Language persistence helpers ─────────────────────────────────────
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WGClientWifiSwitcher", "config.json");

        public static void SaveLanguage(string code)
        {
            try
            {
                AppConfig cfg = new();
                if (File.Exists(ConfigPath))
                {
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), opts) ?? new AppConfig();
                }
                cfg.Language = code;
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.WriteAllText(ConfigPath,
                    JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public static string LoadLanguage()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return "en";
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var cfg  = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), opts);
                return cfg?.Language ?? "en";
            }
            catch { return "en"; }
        }
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

        // WireGuard executable — derived from configured install directory
        private static string WgExe => _cfg.WgExePath;

        // Called by App before MainWindow is created to validate dependencies
        public static string? FindWireGuardExe()
        {
            // Try registry-detected path first
            var detected = DetectWireGuardInstallDir();
            if (detected != null)
            {
                var path = System.IO.Path.Combine(detected, "wireguard.exe");
                if (File.Exists(path)) return path;
            }
            // Fallback: just check Program Files
            var def = @"C:\Program Files\WireGuard\wireguard.exe";
            return File.Exists(def) ? def : null;
        }

        // Auto-detect WireGuard install directory from the registry, then common paths
        private static string? DetectWireGuardInstallDir()
        {
            // 1. Registry: HKLM\SOFTWARE\WireGuard — InstallDirectory value
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WireGuard");
                if (key?.GetValue("InstallDirectory") is string dir && Directory.Exists(dir))
                    return dir.TrimEnd('\\', '/');
            }
            catch { }

            // 2. Registry: uninstall entry written by the WireGuard NSIS installer
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WireGuard");
                if (key?.GetValue("InstallLocation") is string dir && Directory.Exists(dir))
                    return dir.TrimEnd('\\', '/');
            }
            catch { }

            // 3. Well-known default paths
            foreach (var candidate in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard"),
                @"C:\WireGuard",
            })
            {
                if (File.Exists(Path.Combine(candidate, "wireguard.exe")))
                    return candidate;
            }

            return null;
        }

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

            // Configured install dir → derive conf path
            if (!string.IsNullOrWhiteSpace(_cfg.ConfDirectory))
                candidates.Add(_cfg.ConfDirectory);

            // Registry auto-detect
            var detected = DetectWireGuardInstallDir();
            if (detected != null)
            {
                var detectedConf = Path.Combine(detected, "Data", "Configurations");
                if (!candidates.Contains(detectedConf, StringComparer.OrdinalIgnoreCase))
                    candidates.Add(detectedConf);
            }
            candidates.AddRange(new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),       "WireGuard", "Data", "Configurations"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),  "WireGuard", "Data", "Configurations"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WireGuard", "Data", "Configurations"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),       "WireGuard"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),  "WireGuard"),
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
                        // Back-derive the install directory from the conf folder
                        var expectedSuffix = Path.DirectorySeparatorChar + "Data" +
                                             Path.DirectorySeparatorChar + "Configurations";
                        if (candidate.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
                        {
                            var install = candidate.Substring(0, candidate.Length - expectedSuffix.Length);
                            if (!string.Equals(_cfg.InstallDirectory, install, StringComparison.OrdinalIgnoreCase))
                                _cfg.InstallDirectory = install;
                        }
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
                LogRaw("ERROR: " + e.Exception.Message, LogLevel.Warn);
                e.Handled = true;
            };

            Loaded += (_, _) =>
            {
                // Populate language picker
                foreach (var (code, name) in Lang.AvailableLanguages())
                    LanguagePicker.Items.Add(new LangItem(code, name));
                LanguagePicker.DisplayMemberPath = "Name";
                LanguagePicker.SelectedItem = LanguagePicker.Items
                    .Cast<LangItem>().FirstOrDefault(i => i.Code == Lang.Instance.CurrentCode)
                    ?? LanguagePicker.Items.Cast<LangItem>().FirstOrDefault();

                // Refresh TunnelEntry labels when language changes
                Lang.Instance.LanguageChanged += (_, _) =>
                    Dispatcher.BeginInvoke(() =>
                    {
                        foreach (var t in _tunnels) t.RefreshLabels();
                        UpdateAdminLabel();
                        RebuildLog();
                    });

                LoadConfig();
                UpdateAdminLabel();
                SetupTimer();
                Log("LogAppStarted", LogLevel.Info);
            };
        }

        // ── Taskbar icon (force AppWindow style so it appears without WindowStyle) ──

        private const int GWL_EXSTYLE      = -20;
        private const int WS_EX_APPWINDOW  = 0x00040000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);

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

            // Set window icon to match the tray icon.
            // Important: do NOT wrap the icon in a using block — CreateBitmapSourceFromHIcon
            // needs the HICON to stay alive until WPF has copied the pixels. Instead,
            // extract the handle, create the BitmapSource, then destroy the handle manually.
            try
            {
                var drawingIcon = TrayIconHelper.CreateIcon(false);
                var hIcon = drawingIcon.Handle;
                Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    hIcon,
                    System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                DestroyIcon(hIcon);
                drawingIcon.Dispose();
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

            // Set initial state — detect WiFi and apply rules immediately
            var wifi = GetCurrentSsid();
            _lastWifi = wifi;
            UpdateStatusDisplay(wifi);
            if (wifi != null)
            {
                Log("LogStartupWifi", LogLevel.Info, wifi);
                ApplyRules(wifi);
            }
            else
            {
                Log("LogStartupNoWifi", LogLevel.Info);
                ApplyRules(null);
            }
        }

        // Called by WlanApi callback when connection state changes
        private void OnWifiChanged()
        {
            var wifi = GetCurrentSsid();
            UpdateStatusDisplay(wifi);
            if (wifi == _lastWifi) return;
            _lastWifi = wifi;
            Log("LogWifiChanged", LogLevel.Info, wifi ?? Lang.T("LogWifiDisconnected"));
            ApplyRules(wifi);
        }

        public void UpdateStatusDisplay(string? wifi = null)
        {
            wifi ??= GetCurrentSsid();
            WifiLabel.Text       = wifi ?? Lang.T("StatusNone");
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
                if (result != 0) { Log("LogWlanUnavailable", LogLevel.Warn, result); _wlanHandle = IntPtr.Zero; return; }

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
                Log("LogWlanActive", LogLevel.Ok);
            }
            catch (Exception ex) { Log("LogWlanError", LogLevel.Warn, ex.Message); }
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
                    Log("LogRuleMatchedDisconnect", LogLevel.Ok, ssid ?? "");
                    DisconnectAll();
                }
                else
                {
                    Log("LogRuleMatchedActivate", LogLevel.Ok, ssid ?? "", match.Tunnel);
                    SwitchTo(match.Tunnel);
                }
            }
            else
            {
                switch (_cfg.DefaultAction)
                {
                    case "disconnect":
                        Log("LogNoRuleDisconnect", LogLevel.Info, ssid ?? Lang.T("LogWifiDisconnected"));
                        DisconnectAll();
                        break;
                    case "activate" when !string.IsNullOrEmpty(_cfg.DefaultTunnel):
                        Log("LogNoRuleActivate", LogLevel.Info, ssid ?? Lang.T("LogWifiDisconnected"), _cfg.DefaultTunnel);
                        SwitchTo(_cfg.DefaultTunnel);
                        break;
                    default:
                        Log("LogNoRuleNothing", LogLevel.Info, ssid ?? Lang.T("LogWifiDisconnected"));
                        break;
                }
            }
        }

        private void DisconnectAll()
        {
            foreach (var name in GetActiveTunnelNames())
                Log("LogStoppedTunnel", LogLevel.Warn, name, StopTunnel(name) ? Lang.T("LogStoppedTunnelOk") : Lang.T("LogStoppedTunnelFail"));
        }

        private void SwitchTo(string target)
        {
            foreach (var name in GetActiveTunnelNames().Where(n => n != target))
            {
                StopTunnel(name);
                Log("LogStoppedTunnel", LogLevel.Warn, name, Lang.T("LogStoppedTunnelOk"));
            }

            if (GetTunnelStatus(target))
            {
                Log("LogAlreadyActive", LogLevel.Info, target);
                return;
            }

            LastError = "";
            bool ok = StartTunnel(target);
            Log("LogStartedTunnel", ok ? LogLevel.Ok : LogLevel.Warn, target, ok ? Lang.T("LogTunnelOk") : Lang.T("LogTunnelFailed"));
            if (!ok && !string.IsNullOrEmpty(LastError)) LogRaw("  " + LastError, LogLevel.Warn);
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
                // netsh on modern Windows outputs UTF-8 when the active code page is UTF-8
                // (chcp 65001), but falls back to the OEM codepage on older systems.
                // We try UTF-8 first; if the result contains the replacement character
                // (U+FFFD) we re-run with the OEM codepage so special chars in SSIDs
                // (em dash, accented letters, etc.) are decoded correctly.
                var ssid = RunNetsh(System.Text.Encoding.UTF8);
                if (ssid != null && ssid.Contains('\uFFFD'))
                    ssid = RunNetsh(System.Text.Encoding.GetEncoding(
                        System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage));
                return ssid;
            }
            catch { }
            return null;
        }

        private static string? RunNetsh(System.Text.Encoding enc)
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardOutputEncoding = enc
            };
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
                Log("LogConfigLoadError", LogLevel.Warn, ex.Message);
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
                Log("LogConfigSaved", LogLevel.Ok, _cfg.Rules.Count);
            }
            catch (Exception ex)
            {
                Log("LogConfigSaveError", LogLevel.Warn, ex.Message);
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
                Log("LogRuleAdded", LogLevel.Ok, dlg.ResultSsid,
                    string.IsNullOrEmpty(dlg.ResultTunnel) ? Lang.T("TunnelBtnDisconnect") : dlg.ResultTunnel);
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
                Log("LogRuleUpdated", LogLevel.Info, dlg.ResultSsid,
                    string.IsNullOrEmpty(dlg.ResultTunnel) ? Lang.T("TunnelBtnDisconnect") : dlg.ResultTunnel);
            }
        }

        private void DeleteRule_Click(object sender, RoutedEventArgs e)
        {
            if (RulesListView.SelectedItem is not TunnelRule rule) return;
            if (System.Windows.MessageBox.Show(
                    Lang.T("RuleDialogSsidRequired") + "\n" + rule.Ssid + "?",
                    Lang.T("BtnDeleteRule"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _rules.Remove(rule);
                SaveConfig();
                Log("LogRuleDeleted", LogLevel.Warn, rule.Ssid);
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

            LogRaw(Lang.T("LogTunnelDiscovery", tunnels.Count) +
                (tunnels.Count > 0 ? Lang.T("LogTunnelDiscoveryList", string.Join(", ", tunnels)) : "") +
                Lang.T("LogInstallDir", _cfg.InstallDirectory ?? "none"), LogLevel.Info);

            // Update DefaultTunnelBox ComboBox
            var prev = DefaultTunnelBox.Text;
            DefaultTunnelBox.Items.Clear();
            foreach (var t in tunnels) DefaultTunnelBox.Items.Add(t);
            DefaultTunnelBox.Text = prev;

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
            Log("LogManualConnect", LogLevel.Info, tunnel);
            LastError = "";
            bool ok = StartTunnel(tunnel);
            Log("LogManualConnectResult", ok ? LogLevel.Ok : LogLevel.Warn, tunnel, ok ? Lang.T("TunnelStatusConnected") : Lang.T("LogTunnelFailed"));
            if (!ok && !string.IsNullOrEmpty(LastError)) LogRaw("  " + LastError, LogLevel.Warn);
        }

        public void ManualStop(string tunnel)
        {
            Log("LogManualDisconnect", LogLevel.Info, tunnel);
            bool ok = StopTunnel(tunnel);
            Log("LogManualConnectResult", ok ? LogLevel.Ok : LogLevel.Warn, tunnel, ok ? Lang.T("TunnelStatusDisconnected") : Lang.T("LogTunnelFailed"));
        }

        private void TunnelToggle_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as System.Windows.Controls.Button)?.DataContext is not TunnelEntry entry) return;
            if (entry.Active) ManualStop(entry.Name);
            else              ManualStart(entry.Name);
            UpdateStatusDisplay();
        }

        private void OpenWireGuardGui_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exe = WgExe;
                if (!File.Exists(exe))
                {
                    Log("LogGuiNotFound", LogLevel.Warn, exe);
                    return;
                }
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                Log("LogOpenedGui", LogLevel.Ok);
            }
            catch (Exception ex) { Log("LogGuiError", LogLevel.Warn, ex.Message); }
        }

        private void ShowWireGuardLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exe = WgExe;
                if (!File.Exists(exe))
                {
                    Log("LogGuiNotFound", LogLevel.Warn, exe);
                    return;
                }

                // Frameless dark window matching app style
                var bgBrush   = (SolidColorBrush)FindResource("Bg");
                var panelBrush = (SolidColorBrush)FindResource("Panel");
                var borderBrush = (SolidColorBrush)FindResource("Border");
                var cardBrush  = (SolidColorBrush)FindResource("Card");
                var textBrush  = (SolidColorBrush)FindResource("Text");
                var accentBrush = (SolidColorBrush)FindResource("Accent");

                var tb = new System.Windows.Controls.TextBox
                {
                    IsReadOnly          = true,
                    FontFamily          = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize            = 11,
                    Background          = cardBrush,
                    Foreground          = textBrush,
                    BorderThickness     = new Thickness(0),
                    VerticalScrollBarVisibility   = System.Windows.Controls.ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                    TextWrapping        = System.Windows.TextWrapping.NoWrap,
                    Padding             = new Thickness(10),
                    Text                = ""
                };

                // Title bar
                var titleText = new TextBlock
                {
                    Text       = Lang.T("LogWindowTitle"),
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize   = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = accentBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin     = new Thickness(14, 0, 0, 0)
                };

                var tailLabel = new TextBlock
                {
                    Text       = Lang.T("LogWindowLive"),
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize   = 10,
                    Foreground = (SolidColorBrush)FindResource("Green"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin     = new Thickness(0, 0, 12, 0)
                };

                var closeBtn = new System.Windows.Controls.Button
                {
                    Content         = "✕",
                    Style           = (Style)FindResource("DangerBtn"),
                    Padding         = new Thickness(10, 4, 10, 4),
                    BorderThickness = new Thickness(0),
                    Background      = Brushes.Transparent,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin          = new Thickness(0, 0, 4, 0)
                };

                var titleBar = new System.Windows.Controls.Grid
                {
                    Background = panelBrush,
                    Height     = 44
                };
                titleBar.Children.Add(titleText);
                var rightStack = new System.Windows.Controls.StackPanel
                {
                    Orientation       = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment   = VerticalAlignment.Center
                };
                rightStack.Children.Add(tailLabel);
                rightStack.Children.Add(closeBtn);
                titleBar.Children.Add(rightStack);

                var outerBorder = new System.Windows.Controls.Border
                {
                    BorderBrush     = borderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(6)
                };

                var root = new System.Windows.Controls.Grid();
                root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                System.Windows.Controls.Grid.SetRow(titleBar, 0);
                System.Windows.Controls.Grid.SetRow(tb, 1);
                root.Children.Add(titleBar);
                root.Children.Add(tb);
                outerBorder.Child = root;

                var win = new Window
                {
                    Title               = Lang.T("LogWindowTitle"),
                    Width               = 920,
                    Height              = 620,
                    Background          = bgBrush,
                    WindowStyle         = WindowStyle.None,
                    AllowsTransparency  = true,
                    ResizeMode          = ResizeMode.CanResizeWithGrip,
                    Owner               = this,
                    Content             = outerBorder
                };

                closeBtn.Click += (_, _) => win.Close();
                titleBar.MouseLeftButtonDown += (_, mev) => { if (mev.LeftButton == System.Windows.Input.MouseButtonState.Pressed) win.DragMove(); };

                // Helper: filter raw dumplog output to lines from the last 24 hours
                string[] FilterRecent(string raw)
                {
                    var cutoff = DateTime.Now.AddDays(-1);
                    return raw.Split('\n').Where(l =>
                    {
                        if (l.Length >= 19 &&
                            DateTime.TryParseExact(l.Substring(0, 19), "yyyy/MM/dd HH:mm:ss",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out var dt))
                            return dt >= cutoff;
                        return l.TrimEnd().Length > 0;
                    }).ToArray();
                }

                // Step 1: load last 24h history with a plain /dumplog (exits immediately)
                try
                {
                    var histPsi = new ProcessStartInfo(exe, "/dumplog")
                    {
                        UseShellExecute        = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow         = true
                    };
                    using var histProc = Process.Start(histPsi)!;
                    var histText = histProc.StandardOutput.ReadToEnd();
                    histProc.WaitForExit(8000);
                    var histLines = FilterRecent(histText);
                    if (histLines.Length > 0)
                    {
                        tb.Text = string.Join("\n", histLines) + "\n";
                        tb.ScrollToEnd();
                    }
                }
                catch { /* if history fails, start fresh */ }

                // Step 2: start /dumplog /tail — streams new lines to stdout as they arrive
                var tailProc = new Process
                {
                    StartInfo = new ProcessStartInfo(exe, "/dumplog /tail")
                    {
                        UseShellExecute        = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        CreateNoWindow         = true
                    },
                    EnableRaisingEvents = true
                };

                tailProc.OutputDataReceived += (_, args) =>
                {
                    if (string.IsNullOrEmpty(args.Data)) return;
                    var line = args.Data;
                    Dispatcher.BeginInvoke(() => { tb.AppendText(line + "\n"); tb.ScrollToEnd(); });
                };
                tailProc.ErrorDataReceived += (_, args) =>
                {
                    if (string.IsNullOrEmpty(args.Data)) return;
                    var line = args.Data;
                    Dispatcher.BeginInvoke(() => { tb.AppendText(line + "\n"); tb.ScrollToEnd(); });
                };
                tailProc.Exited += (_, _) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        tailLabel.Text       = Lang.T("LogWindowStopped");
                        tailLabel.Foreground = (SolidColorBrush)FindResource("Sub");
                    });
                };

                tailProc.Start();
                tailProc.BeginOutputReadLine();
                tailProc.BeginErrorReadLine();

                win.Closed += (_, _) =>
                {
                    try { if (!tailProc.HasExited) tailProc.Kill(); } catch { }
                    tailProc.Dispose();
                };

                win.Show();
                Log("LogLogOpened", LogLevel.Ok);
            }
            catch (Exception ex) { Log("LogLogError", LogLevel.Warn, ex.Message); }
        }

        // ── Logging ────────────────────────────────────────────────────────────

        private enum LogLevel { Info, Ok, Warn }
        private static readonly SolidColorBrush LInfo = new(Color.FromRgb(88,  166, 255));
        private static readonly SolidColorBrush LOk   = new(Color.FromRgb(63,  185,  80));
        private static readonly SolidColorBrush LWarn = new(Color.FromRgb(247, 129, 102));
        private static readonly SolidColorBrush LTime = new(Color.FromRgb(48,   54,  61));

        // A log entry is either a translatable key+args pair, or a raw (external) string.
        private record LogEntry(DateTime Time, LogLevel Level, string? Key, object[]? Args, string? Raw);

        private readonly List<LogEntry> _logEntries = new();
        private const int MaxLogEntries = 300;

        // Log a translatable message by key (app-generated messages)
        private void Log(string key, LogLevel level, params object[] args)
        {
            var entry = new LogEntry(DateTime.Now, level, key, args, null);
            _logEntries.Insert(0, entry);
            if (_logEntries.Count > MaxLogEntries) _logEntries.RemoveAt(_logEntries.Count - 1);
            RenderLogEntry(entry, prepend: true);
        }

        // Log a raw untranslatable string (e.g. OS error messages, WireGuard internals)
        private void LogRaw(string message, LogLevel level)
        {
            var entry = new LogEntry(DateTime.Now, level, null, null, message);
            _logEntries.Insert(0, entry);
            if (_logEntries.Count > MaxLogEntries) _logEntries.RemoveAt(_logEntries.Count - 1);
            RenderLogEntry(entry, prepend: true);
        }

        private void RenderLogEntry(LogEntry entry, bool prepend)
        {
            try
            {
                var text = entry.Key != null
                    ? (entry.Args?.Length > 0 ? Lang.T(entry.Key, entry.Args) : Lang.T(entry.Key))
                    : (entry.Raw ?? "");

                var para = new Paragraph { Margin = new Thickness(0) };
                para.Inlines.Add(new Run(entry.Time.ToString("HH:mm:ss") + "  ") { Foreground = LTime });
                para.Inlines.Add(new Run(text) { Foreground = entry.Level switch
                    { LogLevel.Ok => LOk, LogLevel.Warn => LWarn, _ => LInfo } });

                if (prepend)
                {
                    if (LogDocument.Blocks.FirstBlock != null)
                        LogDocument.Blocks.InsertBefore(LogDocument.Blocks.FirstBlock, para);
                    else
                        LogDocument.Blocks.Add(para);
                }
                else
                {
                    LogDocument.Blocks.Add(para);
                }
            }
            catch { }
        }

        // Rebuild the entire log display in the current language
        private void RebuildLog()
        {
            try
            {
                LogDocument.Blocks.Clear();
                foreach (var entry in _logEntries)
                    RenderLogEntry(entry, prepend: false);
            }
            catch { }
        }

        // ── Window chrome ──────────────────────────────────────────────────────

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }

        private void LanguagePicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (LanguagePicker.SelectedItem is LangItem item)
                Lang.Instance.Load(item.Code);
        }

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
            AdminLabel.Text       = admin ? Lang.T("LogAdminYes") : Lang.T("LogAdminNo");
            AdminLabel.Foreground = admin ? (SolidColorBrush)FindResource("Green") : (SolidColorBrush)FindResource("Red");
        }
    }

    // Simple data class for the language picker ComboBox
    public class LangItem
    {
        public string Code { get; }
        public string Name { get; }
        public LangItem(string code, string name) { Code = code; Name = name; }
        public override string ToString() => Name;
    }
}
