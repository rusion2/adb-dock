using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AdbDock.Core;

namespace AdbDock.App;

public partial class MainWindow
{
    private readonly DispatcherTimer leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private long? outsideSince;
    private NativeRect? expandedFromTab;
    private bool collapsed;
    private bool titleDragging;
    private bool titleDragCanceled;
    private bool keyboardNavigation;
    private bool draggingEdge;
    private NativePoint? pressStart;
    private NativeRect pressBounds;
    private DockSide pressSide;
    private string? edgeSignature;

    private void InitializeInteraction()
    {
        leaveTimer.Tick += (_, _) => EvaluateAutoCollapse(Desktop.Cursor, Environment.TickCount64);
        previewTimer.Tick += (_, _) => EvaluatePreviewLeave(Desktop.Cursor, Environment.TickCount64);
        IsVisibleChanged += (_, _) => UpdateLeaveTimer();
        MouseEnter += (_, _) => { outsideSince = null; previewOutsideSince = null; };
        PreviewMouseDown += (_, _) => keyboardNavigation = false;
        SourceInitialized += (_, _) =>
        {
            Desktop.ToolWindow(this);
            HwndSource.FromHwnd(Desktop.Handle(this))?.AddHook(WindowMessage);
        };
    }

    private void UpdateLeaveTimer()
    {
        leaveTimer.IsEnabled = loaded && IsVisible && !exiting && !collapsed && settings.ShowFloatingTab && settings.AutoCollapse;
        if (!leaveTimer.IsEnabled) outsideSince = null;
    }

    private void EvaluateAutoCollapse(NativePoint cursor, long nowMs)
    {
        if (!leaveTimer.IsEnabled || interactionDepth > 0 || titleDragging || DeviceSwitcher.IsDropDownOpen ||
            Desktop.Bounds(this).Contains(cursor) || (expandedFromTab?.Contains(cursor) ?? false))
        {
            outsideSince = null;
            return;
        }
        outsideSince ??= nowMs;
        if (nowMs - outsideSince.Value >= settings.AutoCollapseDelaySeconds * 1000L) CollapsePanel();
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (titleDragging && message == 0x0100 && wParam == 0x1B)
            titleDragCanceled = true;
        // DPI, display topology, or a change to the work area (SPI_SETWORKAREA).
        if ((message is 0x02E0 or 0x007E || (message == 0x001A && wParam == 0x002F)) && loaded && !exiting)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (titleDragging || pressStart is not null || exiting) return;
                edgeSignature = null;
                if (collapsed) RenderEdge();
                else
                {
                    FitExpanded();
                    Desktop.Clamp(this);
                    var work = Desktop.WorkArea(this);
                    if (expandedFromTab is { } tab &&
                        (tab.Left < work.Left || tab.Top < work.Top || tab.Right > work.Right || tab.Bottom > work.Bottom))
                        expandedFromTab = null;
                }
            });
        return 0;
    }

    private void RestorePlacement()
    {
        FitExpanded();
        var work = Desktop.WorkArea(this);
        var bounds = Desktop.Bounds(this);
        Desktop.Place(this, settings.WindowX ?? work.Right - bounds.Width - 32, settings.WindowY ?? work.Top + Math.Max(20, (work.Height - bounds.Height) / 2));
        FitExpanded();
        Desktop.Clamp(this);
        UpdateView();
    }
    private void FitExpanded()
    {
        var scale = Desktop.Scale(this);
        var work = Desktop.WorkArea(this);
        Width = Math.Max(240, Math.Min(380, work.Width / scale - 16));
        Height = Math.Max(240, Math.Min(552, work.Height / scale - 16));
    }

    private void RenderEdge()
    {
        if (Desktop.Handle(this) == 0 || pressStart is not null) return;
        var horizontal = settings.Side is DockSide.Top or DockSide.Bottom;
        var work = Desktop.WorkArea(this);
        var scale = Desktop.Scale(this);
        var compactBase = horizontal ? 110 : 132;
        var axisLength = (horizontal ? work.Width : work.Height) / scale;
        // A +N entry uses one complete device slot, including at a short edge.
        var slots = Math.Clamp((int)((axisLength - compactBase - 16) / 39), 1, 6);
        var capacity = Math.Min(5, slots);
        if (model.EdgeDevices.Count > capacity) capacity = Math.Min(5, slots - 1);
        var rows = model.EdgeDevices.Take(capacity).ToArray();
        var extra = model.EdgeDevices.Count - rows.Length;
        var signature = $"{settings.Side}:{capacity}:{model.AdbAvailable}:{model.OnlineCount}:{model.TotalCount}:{model.ShortStatus}:" + string.Join("|", rows.Select(r => r.Tooltip)) + ":" + extra;
        if (signature == edgeSignature) return;
        edgeSignature = signature;
        var old = Desktop.Bounds(this);
        EdgePanel.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        EdgeDevices.Orientation = EdgePanel.Orientation;
        EdgeSeparator.Width = horizontal ? 1 : double.NaN;
        EdgeSeparator.Height = horizontal ? double.NaN : 1;
        EdgeSeparator.Margin = horizontal ? new Thickness(7, 0, 7, 0) : new Thickness(0, 8, 0, 8);
        EdgeSummary.Height = horizontal ? 48 : 62;
        EdgeCounts.Margin = horizontal ? new Thickness(0, 1, 0, 0) : new Thickness(0, 5, 0, 3);
        EdgeSummary.Width = horizontal ? 44 : 36;
        EdgeArrow.Width = horizontal ? 32 : 36;
        EdgeArrow.Height = horizontal ? 36 : 32;
        EdgeArrow.Margin = horizontal ? new Thickness(3, 0, 0, 0) : new Thickness(0, 3, 0, 0);
        EdgeDevices.Children.Clear();
        foreach (var row in rows)
        {
            var phone = new Grid { Width = 23, Height = 27 };
            phone.Children.Add(new Rectangle { Width = 12, Height = 21, RadiusX = 2.5, RadiusY = 2.5, Stroke = row.Tone, StrokeThickness = 1.5 });
            phone.Children.Add(new TextBlock { Text = row.Marker, Foreground = row.Tone, Background = row.Tint, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
            var button = new Button { Content = phone, Background = row.Tint, BorderBrush = row.Tint, Width = 34, Height = 34, Padding = new Thickness(0), Margin = horizontal ? new Thickness(0, 0, 5, 0) : new Thickness(0, 0, 0, 5), DataContext = row };
            System.Windows.Automation.AutomationProperties.SetName(button, row.Tooltip);
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "EdgeDevice:" + row.Serial);
            button.ToolTip = new ToolTip { Content = row.Tooltip, Placement = settings.Side switch { DockSide.Left => PlacementMode.Right, DockSide.Top => PlacementMode.Bottom, DockSide.Bottom => PlacementMode.Top, _ => PlacementMode.Left } };
            ToolTipService.SetInitialShowDelay(button, 0);
            ToolTipService.SetShowDuration(button, 20000);
            AttachPreviewGesture(button, row);
            EdgeDevices.Children.Add(button);
        }
        if (extra > 0 || rows.Length == 0)
        {
            var button = new Button { Content = extra > 0 ? "+" + extra : model.AdbAvailable ? "○" : "!", Width = 34, Height = 34, Padding = new Thickness(0), Margin = horizontal ? new Thickness(0, 0, 5, 0) : new Thickness(0, 0, 0, 5), Foreground = model.AggregateTone };
            button.Click += (_, _) => { model.Select(null); ExpandPanel(true); };
            button.ToolTip = extra > 0 ? "展开全部设备" : model.EmptyMessage;
            System.Windows.Automation.AutomationProperties.SetName(button, extra > 0 ? "展开其余设备" : model.EmptyTitle);
            EdgeDevices.Children.Add(button);
        }
        if (!collapsed) return;
        Width = horizontal ? compactBase + EdgeDevices.Children.Count * 39 : 52;
        Height = horizontal ? 64 : compactBase + EdgeDevices.Children.Count * 39;
        ApplyEdgePosition(old.Left + old.Width / 2, old.Top + old.Height / 2, work);
    }

    private void ApplyEdgePosition(int centerX, int centerY, NativeRect work)
    {
        var scale = Desktop.Scale(this);
        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(Height * scale);
        var x = settings.Side == DockSide.Left ? work.Left : settings.Side == DockSide.Right ? work.Right - width : Math.Clamp(centerX - width / 2, work.Left + 8, Math.Max(work.Left + 8, work.Right - width - 8));
        var y = settings.Side == DockSide.Top ? work.Top : settings.Side == DockSide.Bottom ? work.Bottom - height : Math.Clamp(centerY - height / 2, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - height - 8));
        Desktop.Place(this, x, y, width, height);
        if (devicePreview is { } preview) PlacePreview(preview);
    }

    private void CollapsePanel()
    {
        if (collapsed || interactionDepth > 0 || pressStart is not null || titleDragging || !IsVisible) return;
        if (!settings.ShowFloatingTab) return;
        var bounds = expandedFromTab ?? Desktop.Bounds(this);
        var center = new NativePoint { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 };
        var work = Desktop.WorkArea(this);
        if (settings.Side == DockSide.None) settings.Side = Desktop.Nearest(center, work);
        collapsed = true;
        expandedFromTab = null;
        UpdateLeaveTimer();
        ExpandedPanel.Visibility = Visibility.Collapsed;
        EdgePanel.Visibility = Visibility.Visible;
        Frame.CornerRadius = settings.Side switch { DockSide.Left => new(0, 14, 14, 0), DockSide.Right => new(14, 0, 0, 14), DockSide.Top => new(0, 0, 14, 14), _ => new(14, 14, 0, 0) };
        edgeSignature = null;
        RenderEdge();
        ApplyEdgePosition(center.X, center.Y, work);
        if (keyboardNavigation && IsActive) EdgeSummary.Focus();
        SaveSettings();
    }
    private void ExpandPanel(bool focus)
    {
        if (pressStart is not null) return;
        devicePreview?.Close();
        outsideSince = null;
        if (collapsed)
        {
            var previous = Desktop.Bounds(this);
            expandedFromTab = previous;
            var work = Desktop.WorkArea(this);
            var scale = Desktop.Scale(this);
            collapsed = false;
            ExpandedPanel.Visibility = Visibility.Visible;
            EdgePanel.Visibility = Visibility.Collapsed;
            Frame.CornerRadius = new CornerRadius(18);
            FitExpanded();
            var width = (int)Math.Round(Width * scale); var height = (int)Math.Round(Height * scale);
            var x = settings.Side == DockSide.Left ? work.Left : settings.Side == DockSide.Right ? work.Right - width : previous.Left + previous.Width / 2 - width / 2;
            var y = settings.Side == DockSide.Top ? work.Top : settings.Side == DockSide.Bottom ? work.Bottom - height : previous.Top + previous.Height / 2 - height / 2;
            x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
            y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
            Desktop.Place(this, x, y, width, height);
        }
        UpdateView();
        if (focus) { Activate(); CloseButton.Focus(); }
    }
    private void ClosePanelClick(object sender, RoutedEventArgs e) => Close();

    private void FloatingTabChanged(object sender, RoutedEventArgs e)
    {
        var enabled = FloatButton.IsChecked == true;
        if (settings.ShowFloatingTab == enabled) return;
        settings.ShowFloatingTab = enabled;
        outsideSince = null;
        UpdateView();
        SaveSettings();
        model.Feedback(enabled ? "已开启浮窗，关闭面板后显示在屏幕边缘" : "已关闭浮窗，可从托盘重新打开面板");
    }
    private void EdgeArrowClick(object sender, RoutedEventArgs e) { model.Select(null); ExpandPanel(true); }

    private static bool IsControl(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
    private void TitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || IsControl(e.OriginalSource as DependencyObject)) return;
        var original = Desktop.Bounds(this);
        var originalSide = settings.Side;
        outsideSince = null;
        titleDragCanceled = false;
        titleDragging = true;
        try { DragMove(); }
        catch (InvalidOperationException) { titleDragCanceled = true; }
        finally { titleDragging = false; }
        if (titleDragCanceled || Mouse.LeftButton == MouseButtonState.Pressed)
        {
            settings.Side = originalSide;
            Desktop.Place(this, original.Left, original.Top, original.Width, original.Height);
            Desktop.Clamp(this);
            return;
        }
        var current = Desktop.Bounds(this);
        var moved = Math.Max(Math.Abs(current.Left - original.Left), Math.Abs(current.Top - original.Top));
        // Clicking an already expanded card near its old edge must keep it open.
        if (moved < 3 * Desktop.Scale(this)) return;
        FinishTitleDrag(original);
    }

    private void FinishTitleDrag(NativeRect original)
    {
        if (collapsed || interactionDepth > 0 || pressStart is not null || !IsVisible) return;
        expandedFromTab = null;
        outsideSince = null;
        var bounds = Desktop.Bounds(this);
        var work = Desktop.WorkArea(this);
        var scale = Desktop.Scale(this);
        var edges = new[]
        {
            (Side: DockSide.Left, Gap: bounds.Left - work.Left, Toward: original.Left - bounds.Left),
            (Side: DockSide.Right, Gap: work.Right - bounds.Right, Toward: bounds.Right - original.Right),
            (Side: DockSide.Top, Gap: bounds.Top - work.Top, Toward: original.Top - bounds.Top),
            (Side: DockSide.Bottom, Gap: work.Bottom - bounds.Bottom, Toward: bounds.Bottom - original.Bottom)
        };
        settings.Side = edges.Where(edge => edge.Gap <= 20 * scale && edge.Toward >= 3 * scale)
            .OrderBy(edge => edge.Gap).Select(edge => edge.Side).FirstOrDefault();
        if (settings.Side != DockSide.None)
            Desktop.Place(this, settings.Side == DockSide.Left ? work.Left : settings.Side == DockSide.Right ? work.Right - bounds.Width : bounds.Left, settings.Side == DockSide.Top ? work.Top : settings.Side == DockSide.Bottom ? work.Bottom - bounds.Height : bounds.Top);
        Desktop.Clamp(this);
        if (settings.Side != DockSide.None && settings.ShowFloatingTab) CollapsePanel();
        else SaveSettings();
    }

    private NativePoint EventPoint(MouseEventArgs e)
    {
        var point = PointToScreen(e.GetPosition(this));
        return new NativePoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) };
    }
    private void SummaryMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || pressStart is not null) return;
        e.Handled = true;
        BeginSummaryPress(EventPoint(e));
    }
    private void BeginSummaryPress(NativePoint start)
    {
        pressStart = start;
        pressBounds = Desktop.Bounds(this);
        pressSide = settings.Side;
        draggingEdge = false;
        EdgeSummary.Focus();
        if (!EdgeSummary.CaptureMouse()) pressStart = null;
    }
    private void SummaryMouseMove(object sender, MouseEventArgs e)
    {
        if (pressStart is not null && e.LeftButton == MouseButtonState.Pressed) UpdateSummaryDrag(EventPoint(e));
    }
    private void UpdateSummaryDrag(NativePoint cursor)
    {
        if (pressStart is not { } start) return;
        var dx = cursor.X - start.X; var dy = cursor.Y - start.Y;
        if (!draggingEdge)
        {
            var scale = Desktop.Scale(this);
            if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance * scale &&
                Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance * scale) return;
            draggingEdge = true;
            devicePreview?.Close();
            Opacity = .93;
            ToolTipService.SetIsEnabled(EdgePanel, false);
        }
        Desktop.Place(this, pressBounds.Left + dx, pressBounds.Top + dy);
    }
    private void SummaryMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || pressStart is null) return;
        e.Handled = true;
        FinishPress(false, EventPoint(e));
    }
    private void FinishPress(bool cancel, NativePoint? releasePoint = null)
    {
        if (pressStart is null) return;
        if (!cancel && releasePoint is { } release) UpdateSummaryDrag(release);
        pressStart = null;
        Opacity = 1;
        ToolTipService.SetIsEnabled(EdgePanel, true);
        var wasDragging = draggingEdge;
        draggingEdge = false;
        EdgeSummary.ReleaseMouseCapture();
        if (wasDragging && !cancel)
        {
            var cursor = releasePoint ?? Desktop.Cursor; var work = Desktop.WorkArea(cursor);
            settings.Side = Desktop.Nearest(cursor, work);
            edgeSignature = null;
            RenderEdge();
            ApplyEdgePosition(cursor.X, cursor.Y, work);
        }
        else if (cancel && wasDragging)
        {
            settings.Side = pressSide;
            edgeSignature = null;
            RenderEdge();
            Desktop.Place(this, pressBounds.Left, pressBounds.Top);
            Desktop.Clamp(this);
        }
        if (wasDragging) SaveSettings();
    }
    private void SummaryLostCapture(object sender, MouseEventArgs e) { if (pressStart is not null) FinishPress(true); }
    private void SummaryKeyDown(object sender, KeyEventArgs e)
    {
        if (pressStart is not null) return;
        var side = e.Key switch { Key.Left => DockSide.Left, Key.Right => DockSide.Right, Key.Up => DockSide.Top, Key.Down => DockSide.Bottom, _ => DockSide.None };
        if (side == DockSide.None) return;
        e.Handled = true;
        settings.Side = side;
        edgeSignature = null;
        RenderEdge();
        SaveSettings();
    }
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        outsideSince = null;
        keyboardNavigation = true;
        if (e.Key == Key.Escape && pressStart is not null) { FinishPress(true); e.Handled = true; }
        else if (e.Key == Key.Escape && !collapsed && !DeviceSwitcher.IsDropDownOpen && model.Selected is not null)
        {
            model.Select(null);
            UpdateView();
            e.Handled = true;
        }
    }
}
