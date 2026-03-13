using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace WGClientWifiSwitcher.Views
{
    public partial class RuleDialog : Window
    {
        public string ResultSsid   { get; private set; } = "";
        public string ResultTunnel { get; private set; } = "";

        private readonly string? _currentSsid;

        public RuleDialog(string? currentSsid,
                          string existingSsid   = "",
                          string existingTunnel = "",
                          List<string>? tunnels = null)
        {
            InitializeComponent();
            _currentSsid = currentSsid;

            // Populate tunnel dropdown
            TunnelBox.Items.Clear();
            TunnelBox.Items.Add("");   // blank = disconnect
            if (tunnels != null)
                foreach (var t in tunnels) TunnelBox.Items.Add(t);

            if (!string.IsNullOrEmpty(existingSsid))
            {
                DialogTitle.Text = "Edit Rule";
                SsidBox.Text     = existingSsid;
            }

            // Set selected tunnel (editable ComboBox uses Text)
            TunnelBox.Text = existingTunnel;

            SsidBox.Focus();
        }

        private void UseCurrent_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentSsid))
                SsidBox.Text = _currentSsid;
            else
                MessageBox.Show(
                    "Not currently connected to a WiFi network.",
                    "No WiFi", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var ssid = SsidBox.Text.Trim();
            if (string.IsNullOrEmpty(ssid))
            {
                MessageBox.Show(
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
