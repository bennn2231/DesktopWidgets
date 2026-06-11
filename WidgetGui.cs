using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WidgesDesktopDotNet;

internal static class WidgetGui
{
    internal static readonly Color BackgroundTop = Color.FromRgb(13, 13, 13);
    internal static readonly Color BackgroundBottom = Color.FromRgb(42, 42, 42);
    internal static readonly Color BorderColor = Color.FromRgb(156, 163, 175);
    internal static readonly Color ControlText = Color.FromRgb(245, 245, 245);

    internal static Border WidgetBackground(Thickness margin, double radius = 18)
    {
        return new Border
        {
            Margin = margin,
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(BorderColor),
            Background = new LinearGradientBrush(BackgroundTop, BackgroundBottom, 90),
            Opacity = 0.94
        };
    }

    internal static Border IconButton(string icon, double width, double height, string tooltip)
    {
        var path = new Path
        {
            Data = Geometry.Parse(IconPath(icon)),
            Fill = new SolidColorBrush(ControlText),
            Stretch = Stretch.Uniform
        };
        var viewbox = new Viewbox
        {
            Width = Math.Max(8, width * 0.5),
            Height = Math.Max(8, height * 0.64),
            Child = path
        };
        return new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(Math.Min(10, height / 3)),
            Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = viewbox
        };
    }

    internal static string IconPath(string icon)
    {
        return icon.Trim().ToLowerInvariant() switch
        {
            "previous" or "prev" => "M16,4 L8,10 L16,16 Z M8,4 L0,10 L8,16 Z",
            "play" => "M3,2 L17,10 L3,18 Z",
            "pause" => "M4,2 L8,2 L8,18 L4,18 Z M12,2 L16,2 L16,18 L12,18 Z",
            "next" => "M0,4 L8,10 L0,16 Z M8,4 L16,10 L8,16 Z",
            "refresh" => "M15,4 A7,7 0 1 0 17,10 L19,10 A9,9 0 1 1 16.4,3.6 Z M15,0 L20,0 L20,5 Z",
            "settings" => "M10,6 A4,4 0 1 0 10,14 A4,4 0 1 0 10,6 M9,0 L11,0 L12,3 L15,4 L18,2 L20,5 L18,8 L19,10 L22,12 L20,15 L16,14 L14,16 L14,20 L10,21 L8,17 L6,16 L2,17 L0,14 L3,11 L3,9 L0,6 L2,3 L6,4 L8,3 Z",
            _ when icon.StartsWith("M", StringComparison.OrdinalIgnoreCase) => icon,
            _ => "M3,2 L17,10 L3,18 Z"
        };
    }
}

internal sealed class LuaImmediateGui
{
    private readonly Canvas _canvas;
    private readonly Func<string, FontFamily> _fontResolver;
    private readonly List<UIElement> _frameElements = [];
    private readonly List<LuaGuiClickRegion> _clickRegions = [];
    private readonly HashSet<string> _pressedKeys = new(StringComparer.OrdinalIgnoreCase);

    internal LuaImmediateGui(Canvas canvas, Func<string, FontFamily> fontResolver)
    {
        _canvas = canvas;
        _fontResolver = fontResolver;
    }

    internal void BeginFrame()
    {
        foreach (var element in _frameElements)
        {
            _canvas.Children.Remove(element);
        }

        _frameElements.Clear();
        _clickRegions.Clear();
    }

    internal void Panel(double x, double y, double width, double height, string color, double opacity, double radius, string strokeColor, double strokeThickness)
    {
        var element = new Rectangle
        {
            Width = Math.Max(0, width),
            Height = Math.Max(0, height),
            RadiusX = Math.Max(0, radius),
            RadiusY = Math.Max(0, radius),
            Fill = LuaWidgetWindow.NewBrush(color, opacity),
            Stroke = LuaWidgetWindow.NewBrush(strokeColor, opacity),
            StrokeThickness = Math.Max(0, strokeThickness)
        };
        Add(element, x, y);
    }

    internal void Label(string text, double x, double y, double size, string color, string weight, string fontFamily)
    {
        var element = new TextBlock
        {
            Text = text,
            FontFamily = _fontResolver(fontFamily),
            FontSize = Math.Max(1, size),
            FontWeight = string.Equals(weight, "bold", StringComparison.OrdinalIgnoreCase) ? FontWeights.Bold : FontWeights.Normal,
            Foreground = LuaWidgetWindow.NewBrush(color, 1),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Add(element, x, y);
    }

    internal bool Button(string text, double x, double y, double width, double height, string key)
    {
        var border = new Border
        {
            Width = Math.Max(1, width),
            Height = Math.Max(1, height),
            CornerRadius = new CornerRadius(Math.Min(10, Math.Max(0, height / 3))),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(82, 255, 255, 255)),
            Background = new SolidColorBrush(Color.FromArgb(82, 255, 255, 255)),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = _fontResolver("Segoe UI"),
                FontSize = Math.Max(10, Math.Min(16, height * 0.42)),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(WidgetGui.ControlText),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        Add(border, x, y);
        Register(key, x, y, width, height);
        return Consume(key);
    }

    internal bool IconButton(string icon, double x, double y, double width, double height, string key)
    {
        var button = WidgetGui.IconButton(icon, Math.Max(1, width), Math.Max(1, height), key);
        Add(button, x, y);
        Register(key, x, y, width, height);
        return Consume(key);
    }

    internal bool HandleClick(double x, double y)
    {
        for (var i = _clickRegions.Count - 1; i >= 0; i--)
        {
            var region = _clickRegions[i];
            if (x >= region.X && x <= region.X + region.Width && y >= region.Y && y <= region.Y + region.Height)
            {
                _pressedKeys.Add(region.Key);
                return true;
            }
        }

        return false;
    }

    private void Add(FrameworkElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        _canvas.Children.Add(element);
        _frameElements.Add(element);
    }

    private void Register(string key, double x, double y, double width, double height)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        _clickRegions.Add(new LuaGuiClickRegion(key, x, y, Math.Max(1, width), Math.Max(1, height)));
    }

    private bool Consume(string key)
    {
        return !string.IsNullOrWhiteSpace(key) && _pressedKeys.Remove(key);
    }
}

internal sealed record LuaGuiClickRegion(string Key, double X, double Y, double Width, double Height);
