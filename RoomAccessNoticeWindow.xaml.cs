using System.Windows;

namespace LanChat;

public partial class RoomAccessNoticeWindow : Window
{
    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        try { DragMove(); } catch (System.InvalidOperationException) { }
    }

    public RoomAccessNoticeWindow(string message)
    {
        InitializeComponent();
        NoticeText.Text = message;
    }

    private void Pin_Checked(object sender, RoutedEventArgs e) => Topmost = true;
    private void Pin_Unchecked(object sender, RoutedEventArgs e) => Topmost = false;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
