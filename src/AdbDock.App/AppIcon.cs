using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdbDock.App;

public static class AppIcon
{
    private static readonly Uri ResourceUri = new("pack://application:,,,/AdbDock;component/Assets/App.ico");

    // NotifyIcon keeps a reference to this icon. Its caller owns the returned clone
    // and must dispose it only after disposing the NotifyIcon.
    public static System.Drawing.Icon CreateTrayIcon()
    {
        using var stream = OpenResource();
        using var source = new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        return (System.Drawing.Icon)source.Clone();
    }

    public static ImageSource CreateWindowIcon()
    {
        using var stream = OpenResource();
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[decoder.Frames.Count - 1];
        frame.Freeze();
        return frame;
    }

    private static Stream OpenResource() => Application.GetResourceStream(ResourceUri)?.Stream
        ?? throw new FileNotFoundException("找不到 ADB Dock 图标资源。", ResourceUri.ToString());
}
