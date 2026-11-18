using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AdbDock.App;

public sealed class DeviceAliasWindow : Window
{
    public string? Alias { get; private set; }

    public DeviceAliasWindow(DeviceRow row)
    {
        Title = "设备备注";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.White;
        AutomationProperties.SetAutomationId(this, "DeviceAliasWindow");

        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock
        {
            Text = row.ModelName, ToolTip = row.ModelName, FontSize = 16, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis
        });
        root.Children.Add(new TextBlock
        {
            Text = row.Serial, ToolTip = row.Serial, FontFamily = new FontFamily("Consolas"), FontSize = 12,
            Foreground = Palette.Gray, Margin = new Thickness(0, 4, 0, 16),
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis
        });

        var label = new TextBlock { Text = "备注名称", Margin = new Thickness(0, 0, 0, 5) };
        var input = new TextBox { Text = row.Alias ?? "" };
        AutomationProperties.SetAutomationId(input, "DeviceAliasInput");
        AutomationProperties.SetLabeledBy(input, label);
        root.Children.Add(label);
        root.Children.Add(input);
        root.Children.Add(new TextBlock
        {
            Text = "备注保存在这台电脑上。留空后保存可恢复设备名称。",
            Foreground = Palette.Gray, FontSize = 12, Margin = new Thickness(0, 6, 0, 16)
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "保存", IsDefault = true };
        save.SetResourceReference(BackgroundProperty, "Accent");
        save.Foreground = Brushes.White;
        AutomationProperties.SetAutomationId(save, "SaveDeviceAliasButton");
        save.Click += (_, _) =>
        {
            var value = input.Text.Trim();
            Alias = value.Length == 0 ? null : value;
            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        FocusManager.SetFocusedElement(this, input);
        Loaded += (_, _) => input.SelectAll();
    }
}
