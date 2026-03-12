using System.Windows;
using System.Windows.Input;

namespace WGWifiSwitcher.Views
{
    public partial class RuleDialog : Window
    {
        public string ResultSsid   { get; private set; } = "";
        public string ResultTunnel { get; private set; } = "";

        private readonly string? _currentSsid;

        public RuleDialog(string? currentSsid, string existingSsid = "", string existingTunnel = "")
        {
            InitializeComponent();
            _currentSsid = currentSsid;

            if (!string.IsNullOrEmpty(existingSsid))
            {
                DialogTitle.Text = "Edit Rule";
                SsidBox.Text     = existingSsid;
                TunnelBox.Text   = existingTunnel;
            }

            SsidBox.Focus();
        }

        private void UseCurrent_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentSsid))
                SsidBox.Text = _currentSsid;
            else
                System.Windows.MessageBox.Show(
                    "Not currently connected to a WiFi network.",
                    "No WiFi", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var ssid = SsidBox.Text.Trim();
            if (string.IsNullOrEmpty(ssid))
            {
                System.Windows.MessageBox.Show(
                    "Please enter a WiFi network name (SSID).",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                SsidBox.Focus();
                return;
            }
            ResultSsid   = ssid;
            ResultTunnel = TunnelBox.Text.Trim();  // empty = disconnect
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Title_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }
    }
}
