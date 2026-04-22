using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace MasselGUARD.Views
{
    public partial class LanRuleDialog : Window
    {
        public string ResultFilter { get; private set; } = "";
        public string ResultTunnel { get; private set; } = "";

        public LanRuleDialog(List<string>? tunnels = null,
                             string existingFilter = "",
                             string existingTunnel = "")
        {
            InitializeComponent();

            bool isEdit = !string.IsNullOrEmpty(existingFilter);
            DialogTitle.Text = isEdit ? Lang.T("LanRuleEditTitle") : Lang.T("LanRuleAddTitle");
            Title            = DialogTitle.Text;

            FilterBox.Text = existingFilter;

            // Populate tunnel dropdown — empty entry = disconnect
            TunnelBox.Items.Add("");
            if (tunnels != null)
                foreach (var t in tunnels) TunnelBox.Items.Add(t);

            TunnelBox.Text = existingTunnel;
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            var filter = FilterBox.Text.Trim();
            if (string.IsNullOrEmpty(filter))
            {
                ErrorLabel.Text       = Lang.T("LanRuleFilterRequired");
                ErrorLabel.Visibility = Visibility.Visible;
                return;
            }
            ResultFilter = filter;
            ResultTunnel = TunnelBox.Text.Trim();
            DialogResult = true;
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }
    }
}
