using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AdbDock.App;

public partial class DevicePreviewWindow : Window
{
    private readonly Action<DeviceRow, string> execute;
    public DeviceRow Row => (DeviceRow)DataContext;

    public DevicePreviewWindow(DeviceRow row, Action<DeviceRow, string> execute)
    {
        this.execute = execute;
        InitializeComponent();
        SetDevice(row, false);
        SourceInitialized += (_, _) => Desktop.ToolWindow(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || PreviewDeviceSwitcher.IsDropDownOpen) return;
            e.Handled = true;
            Close();
        };
    }

    public void SetDevice(DeviceRow row, bool busy)
    {
        if (DataContext is not DeviceRow old || old.Serial != row.Serial || old.Status != row.Status)
            Feedback.Text = row.IsOnline ? "" : row.Device.State == "unauthorized" ? "请在设备上允许 USB 调试。" : "设备可用后即可操作。";
        DataContext = row;
        ActionGrid.IsEnabled = row.IsOnline && !busy;
    }

    public void SetFeedback(string message) => Feedback.Text = message;

    public void AnimateOpen(bool toLeft)
    {
        if (SystemParameters.ClientAreaAnimation)
            Slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(toLeft ? 24 : -24, 0, TimeSpan.FromMilliseconds(150))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    private void ActionClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Button { Tag: string action } && Row.IsOnline) execute(Row, action);
    }
}
