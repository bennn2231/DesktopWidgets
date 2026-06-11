using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WidgesDesktopDotNet;

internal sealed class ClockWindow : Window
{
    private const double BaseWidth = 555;
    private const double BaseHeight = 220;

    internal TextBlock TimeText { get; }
    internal TextBlock SecondsText { get; }
    internal TextBlock AmPmText { get; }
    internal TextBlock DateText { get; }
    private readonly Border _backgroundPanel;

    internal ClockWindow(WidgetRuntime runtime, int left, int top, int sizePercent, bool backgroundEnabled)
    {
        Left = left;
        Top = top;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
                runtime.OpenSettings();
            else
                WidgetDrag.Begin(this, e, () => { runtime.SaveConfig(); runtime.SendWidgetsToDesktop(); });
        };

        Closing += (_, _) => runtime.SaveConfig();
        ContextMenu = BuildMenu(runtime);

        var root = new Grid { Width = BaseWidth, Height = BaseHeight };
        Content = new Viewbox { Stretch = Stretch.Fill, Child = root };

        _backgroundPanel = NewBackgroundPanel();
        root.Children.Add(_backgroundPanel);

        var timePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -22, 0, 0)
        };
        root.Children.Add(timePanel);

        TimeText = NewText("--:--", 147, FontWeights.Bold, TextAlignment.Center);
        TimeText.LineHeight = 150;
        timePanel.Children.Add(TimeText);

        var rightPanel = new Grid
        {
            Width = 74,
            Height = 158,
            Margin = new Thickness(0, 0, 0, 0)
        };
        timePanel.Children.Add(rightPanel);

        SecondsText = NewText("00", 45, FontWeights.SemiBold, TextAlignment.Left);
        SecondsText.Margin = new Thickness(11, 58, 0, 0);
        SecondsText.VerticalAlignment = VerticalAlignment.Top;
        rightPanel.Children.Add(SecondsText);

        AmPmText = NewText(string.Empty, 40, FontWeights.Bold, TextAlignment.Left);
        AmPmText.Margin = new Thickness(11, 96, 0, 0);
        AmPmText.VerticalAlignment = VerticalAlignment.Top;
        rightPanel.Children.Add(AmPmText);

        DateText = NewText("Loading", 45, FontWeights.Light, TextAlignment.Center);
        DateText.HorizontalAlignment = HorizontalAlignment.Center;
        DateText.VerticalAlignment = VerticalAlignment.Top;
        DateText.Margin = new Thickness(0, 140, 0, 0);
        root.Children.Add(DateText);

        ApplySize(sizePercent);
        SetBackgroundEnabled(backgroundEnabled);
    }

    internal void ApplySize(int sizePercent)
    {
        var scale = Math.Clamp(sizePercent, 70, 150) / 100d;
        Width = BaseWidth * scale;
        Height = BaseHeight * scale;
    }

    internal void SetBackgroundEnabled(bool enabled)
    {
        _backgroundPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Border NewBackgroundPanel()
    {
        return WidgetGui.WidgetBackground(new Thickness(8, 2, 8, 20));
    }

    private static ContextMenu BuildMenu(WidgetRuntime runtime)
    {
        var menu = new ContextMenu();
        menu.Items.Add(NewMenuItem("Refresh Weather", (_, _) => runtime.RefreshWeather()));
        menu.Items.Add(NewMenuItem("Reload Lua Widgets", (_, _) => runtime.ReloadLuaWidgets()));
        menu.Items.Add(NewMenuItem("Lua Editor", (_, _) => runtime.OpenLuaEditor()));
        menu.Items.Add(NewMenuItem("Settings", (_, _) => runtime.OpenSettings()));
        menu.Items.Add(NewMenuItem("Exit", (_, _) =>
        {
            runtime.SaveConfig();
            Application.Current?.Shutdown();
        }));
        return menu;
    }

    private static MenuItem NewMenuItem(string header, RoutedEventHandler click)
    {
        var item = new MenuItem { Header = header };
        item.Click += click;
        return item;
    }

    private static TextBlock NewText(string text, double size, FontWeight weight, TextAlignment align)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = size,
            FontWeight = weight,
            TextAlignment = align
        };
    }
}
