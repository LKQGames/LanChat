using System.Windows;

namespace LanChat;

public partial class RemoteControlConsentWindow : Window
{
    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        try { DragMove(); } catch (System.InvalidOperationException) { }
    }

    public RemoteControlConsentWindow(string requesterName, string requesterIp)
    {
        InitializeComponent();
        RequesterText.Text = $"{requesterName}（{requesterIp}）";
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Decline_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
