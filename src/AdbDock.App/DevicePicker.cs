using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace AdbDock.App;

public sealed class DevicePicker : ComboBox
{
    public sealed record DeviceOption(string Serial, string Name, string Status)
    {
        public string DisplayText => $"{Name} · {Serial} · {Status}";
    }

    private readonly ObservableCollection<DeviceOption> options = new();
    private int settingDevices;

    public event Action<string>? DeviceChosen;

    public DevicePicker()
    {
        ItemsSource = options;
        SelectedValuePath = nameof(DeviceOption.Serial);
        DisplayMemberPath = nameof(DeviceOption.DisplayText);
        TextSearch.SetTextPath(this, nameof(DeviceOption.DisplayText));
        IsEditable = false;
        IsTextSearchEnabled = true;
        MinWidth = 168;
        MaxWidth = 360;
        MinHeight = 34;
        MaxDropDownHeight = 280;
        FontSize = 12;
        Padding = new Thickness(8, 4, 4, 4);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(this, "切换设备");
        AutomationProperties.SetHelpText(this, "根据设备名称和序列号选择操作对象。");
        SetBinding(ToolTipProperty, new Binding("SelectedItem.DisplayText") { Source = this });

        if (!SystemParameters.HighContrast)
        {
            Background = TryFindResource("AccentTint") as Brush ?? Brushes.White;
            Foreground = TryFindResource("Ink") as Brush ?? SystemColors.ControlTextBrush;
            BorderBrush = TryFindResource("Line") as Brush ?? SystemColors.ControlDarkBrush;
        }

        // Keep the native ComboBox template, focus feedback and keyboard behavior.
        var textStyle = new Style(typeof(TextBlock), TryFindResource(typeof(TextBlock)) as Style);
        textStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
        textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        textStyle.Setters.Add(new Setter(MaxWidthProperty, 420d));
        Resources[typeof(TextBlock)] = textStyle;
        var itemStyle = new Style(typeof(ComboBoxItem), TryFindResource(typeof(ComboBoxItem)) as Style);
        itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 6, 8, 6)));
        itemStyle.Setters.Add(new Setter(MinHeightProperty, 32d));
        itemStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(DeviceOption.DisplayText))));
        itemStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding(nameof(DeviceOption.DisplayText))));
        ItemContainerStyle = itemStyle;
    }

    public void SetDevices(IEnumerable<DeviceRow> devices, string? currentSerial)
    {
        ArgumentNullException.ThrowIfNull(devices);
        VerifyAccess();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = devices.Where(row => seen.Add(row.Serial)).ToArray();
        settingDevices++;
        try
        {
            var unchanged = rows.Length == options.Count;
            for (var index = 0; unchanged && index < rows.Length; index++)
            {
                unchanged = options[index].Serial == rows[index].Serial &&
                    options[index].Name == rows[index].Name && options[index].Status == rows[index].Status;
            }
            if (!unchanged)
            {
                var existing = options.ToDictionary(option => option.Serial, StringComparer.Ordinal);
                var next = rows.Select(row => existing.TryGetValue(row.Serial, out var option) &&
                    option.Name == row.Name && option.Status == row.Status
                        ? option : new DeviceOption(row.Serial, row.Name, row.Status)).ToArray();
                for (var index = 0; index < next.Length; index++)
                {
                    if (index < options.Count && ReferenceEquals(options[index], next[index])) continue;
                    var previousIndex = options.IndexOf(next[index]);
                    if (previousIndex >= 0) options.Move(previousIndex, index);
                    else if (index < options.Count && options[index].Serial == next[index].Serial) options[index] = next[index];
                    else options.Insert(index, next[index]);
                }
                while (options.Count > next.Length) options.RemoveAt(options.Count - 1);
            }

            var desiredSerial = currentSerial is not null && seen.Contains(currentSerial) ? currentSerial : null;
            if (!StringComparer.Ordinal.Equals(SelectedValue as string, desiredSerial))
                SetCurrentValue(SelectedValueProperty, desiredSerial);
        }
        finally { settingDevices--; }
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (settingDevices != 0 || SelectedItem is not DeviceOption chosen) return;
        if (!e.AddedItems.OfType<DeviceOption>().Any(option => option.Serial == chosen.Serial) ||
            e.RemovedItems.OfType<DeviceOption>().Any(option => option.Serial == chosen.Serial)) return;
        DeviceChosen?.Invoke(chosen.Serial);
    }
}
