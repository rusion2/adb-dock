using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AdbDock.App;

public partial class MainWindow
{
    private DevicePreviewWindow? devicePreview;
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private long? previewOutsideSince;

    private void AttachPreviewGesture(Button button, DeviceRow row)
    {
        NativePoint? pressedAt = null;
        button.Click += (_, _) => ShowDevicePreview(row);
        button.PreviewMouseLeftButtonDown += (_, e) => pressedAt = EventPoint(e);
        button.LostMouseCapture += (_, _) => pressedAt = null;
        button.PreviewMouseMove += (_, e) =>
        {
            if (pressedAt is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var current = EventPoint(e);
            var left = start.X - current.X;
            if (left < 12 * Desktop.Scale(this) || left < Math.Abs(start.Y - current.Y)) return;
            pressedAt = null;
            button.ReleaseMouseCapture();
            ShowDevicePreview(row);
        };
    }

    private void ShowDevicePreview(DeviceRow row)
    {
        if (!collapsed || !IsVisible || !settings.ShowFloatingTab || interactionDepth != 0) return;
        previewOutsideSince = null;
        var preview = devicePreview;
        if (preview is null)
        {
            preview = new DevicePreviewWindow(row, PreviewAction) { Owner = this, Topmost = Topmost };
            devicePreview = preview;
            preview.PreviewDeviceSwitcher.DeviceChosen += serial =>
            {
                var target = model.Devices.FirstOrDefault(candidate => candidate.Serial == serial);
                if (target is not null) ShowDevicePreview(target);
            };
            preview.PreviewDeviceSwitcher.DropDownClosed += (_, _) => previewOutsideSince = null;
            preview.Closed += (_, _) =>
            {
                previewTimer.Stop();
                devicePreview = null;
                previewOutsideSince = null;
            };
            preview.MouseEnter += (_, _) => previewOutsideSince = null;
            new WindowInteropHelper(preview).EnsureHandle();
            PlacePreview(preview);
            preview.Show();
            preview.AnimateOpen(PlacePreview(preview));
            previewTimer.Start();
        }
        preview.SetDevice(row, busy.Contains(row.Serial));
        preview.PreviewDeviceSwitcher.SetDevices(model.Devices, row.Serial);
    }

    private async void PreviewAction(DeviceRow row, string action)
    {
        var preview = devicePreview;
        if (preview is null || busy.Contains(row.Serial) || !row.IsOnline) return;
        await RunDeviceActionAsync(row, action, preview);
        if (devicePreview == preview && preview.Row.Serial == row.Serial)
            preview.SetFeedback(model.Footer);
    }

    private void RefreshDevicePreview()
    {
        if (devicePreview is not { } preview) return;
        var current = model.Devices.FirstOrDefault(row => row.Serial == preview.Row.Serial);
        if (current is null) { preview.Close(); return; }
        preview.SetDevice(current, busy.Contains(current.Serial));
        preview.PreviewDeviceSwitcher.SetDevices(model.Devices, current.Serial);
    }

    private bool PlacePreview(DevicePreviewWindow preview)
    {
        var tab = Desktop.Bounds(this);
        var work = Desktop.WorkArea(this);
        var scale = Desktop.Scale(this);
        var width = Math.Min(work.Width, (int)Math.Round(preview.Width * scale));
        var height = Math.Min(work.Height, (int)Math.Round(preview.Height * scale));
        int x, y;
        bool toLeft = tab.Left - work.Left >= width;
        if (toLeft || work.Right - tab.Right >= width)
        {
            x = toLeft ? tab.Left - width : tab.Right;
            y = tab.Top + (tab.Height - height) / 2;
        }
        else
        {
            x = tab.Left + (tab.Width - width) / 2;
            y = work.Bottom - tab.Bottom >= height ? tab.Bottom : tab.Top - height;
        }
        Desktop.Place(preview, Math.Clamp(x, work.Left, work.Right - width),
            Math.Clamp(y, work.Top, work.Bottom - height), width, height);
        return toLeft;
    }

    private void EvaluatePreviewLeave(NativePoint cursor, long nowMs)
    {
        if (devicePreview is not { } preview) return;
        if (interactionDepth > 0 || preview.PreviewDeviceSwitcher.IsDropDownOpen ||
            Desktop.Bounds(this).Contains(cursor) || Desktop.Bounds(preview).Contains(cursor))
        {
            previewOutsideSince = null;
            return;
        }
        previewOutsideSince ??= nowMs;
        if (nowMs - previewOutsideSince.Value >= 5000) preview.Close();
    }
}
