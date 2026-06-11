using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WidgesDesktopDotNet;

internal sealed class WeatherWindow : Window
{
    private const double BaseWidth = 594;
    private const double BaseHeight = 194;

    internal Border CurrentIcon { get; }
    internal TextBlock TempText { get; }
    internal TextBlock CityText { get; }
    internal TextBlock ConditionText { get; }
    internal Border Separator { get; }
    private readonly Border _backgroundPanel;
    internal int TrackWidth => 86;

    internal WeatherWindow(WidgetRuntime runtime, int left, int top, int sizePercent, bool backgroundEnabled)
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

        var grid = new Grid { Width = BaseWidth, Height = BaseHeight };
        Content = new Viewbox { Stretch = Stretch.Fill, Child = grid };
        _backgroundPanel = NewBackgroundPanel();
        grid.Children.Add(_backgroundPanel);
        foreach (var width in new[] { 238d, 32d, 324d })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        }

        var leftPanel = new Canvas { Width = 238, Height = 194, RenderTransform = new TranslateTransform(0, -10) };
        Grid.SetColumn(leftPanel, 0);
        grid.Children.Add(leftPanel);

        CurrentIcon = NewTintedIcon(67);
        Canvas.SetLeft(CurrentIcon, 124);
        Canvas.SetTop(CurrentIcon, 42);
        leftPanel.Children.Add(CurrentIcon);

        TempText = NewText("--", 35, FontWeights.Light, TextAlignment.Right);
        TempText.Width = 97;
        Canvas.SetLeft(TempText, 132);
        Canvas.SetTop(TempText, 52);
        leftPanel.Children.Add(TempText);

        CityText = NewText("FORT COLLINS", 26, FontWeights.Light, TextAlignment.Right);
        CityText.Width = 227;
        Canvas.SetLeft(CityText, 0);
        Canvas.SetTop(CityText, 96);
        leftPanel.Children.Add(CityText);

        ConditionText = NewText("Loading", 14, FontWeights.Regular, TextAlignment.Right);
        ConditionText.Width = 227;
        Canvas.SetLeft(ConditionText, 0);
        Canvas.SetTop(ConditionText, 133);
        leftPanel.Children.Add(ConditionText);

        Separator = new Border
        {
            Width = 1,
            Height = 151,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(Separator, 1);
        grid.Children.Add(Separator);

        var forecastStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(13, 0, 0, 0),
            RenderTransform = new TranslateTransform(-10, 0)
        };
        Grid.SetColumn(forecastStack, 2);
        grid.Children.Add(forecastStack);

        var rows = new List<ForecastRowUi>();
        for (var i = 0; i < 7; i++)
        {
            var row = BuildForecastRow();
            forecastStack.Children.Add(row.RowGrid);
            rows.Add(row);
        }

        runtime.RegisterForecastRows(rows);
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
        var panel = WidgetGui.WidgetBackground(new Thickness(8));
        Grid.SetColumnSpan(panel, 3);
        return panel;
    }

    private static ForecastRowUi BuildForecastRow()
    {
        var rowGrid = new Grid { Height = 19, Width = 288 };
        foreach (var width in new[] { 35d, 30d, 42d, 11d, 86d, 11d, 45d })
        {
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        }

        var day = NewText("---", 15, FontWeights.Regular, TextAlignment.Left);
        var icon = NewTintedIcon(37);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var min = NewText("--", 14, FontWeights.Regular, TextAlignment.Right);
        var max = NewText("--", 14, FontWeights.Regular, TextAlignment.Left);

        var canvas = new Canvas { Width = 86, Height = 7, VerticalAlignment = VerticalAlignment.Center };
        var track = new Border { Width = 86, Height = 4, CornerRadius = new CornerRadius(2) };
        var fill = new Border { Width = 0, Height = 4, CornerRadius = new CornerRadius(2) };
        Canvas.SetTop(track, 1);
        Canvas.SetTop(fill, 1);
        canvas.Children.Add(track);
        canvas.Children.Add(fill);

        Grid.SetColumn(day, 0);
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(min, 2);
        Grid.SetColumn(canvas, 4);
        Grid.SetColumn(max, 6);
        rowGrid.Children.Add(day);
        rowGrid.Children.Add(icon);
        rowGrid.Children.Add(min);
        rowGrid.Children.Add(canvas);
        rowGrid.Children.Add(max);

        return new ForecastRowUi(rowGrid, day, icon, min, max, track, fill);
    }

    private static Border NewTintedIcon(double size)
    {
        return new Border { Width = size, Height = size };
    }

    private static TextBlock NewText(string text, double size, FontWeight weight, TextAlignment align)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = size,
            FontWeight = weight,
            TextAlignment = align,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
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
}

internal sealed record ForecastRowUi(
    Grid RowGrid,
    TextBlock Day,
    Border Icon,
    TextBlock Min,
    TextBlock Max,
    Border Track,
    Border Fill
);
