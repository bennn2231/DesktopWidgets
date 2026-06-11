using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WidgesDesktopDotNet;

internal sealed class SpotifyWindow : Window
{
    private const double BaseWidth = 360;
    private const double BaseHeight = 132;
    private const double TitleStartX = 126;
    private const double TitleWidth = 210;

    private readonly WidgetRuntime _runtime;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _marqueeTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly Border _coverFrame;
    private readonly TextBlock _title;
    private readonly TextBlock _artist;
    private readonly TextBlock _status;
    private readonly Path _playIcon;
    private readonly TranslateTransform _titleShift = new();

    private string _titleText = string.Empty;
    private string _currentCoverUrl = string.Empty;
    private bool _isPlaying;
    private double _titleOffset;
    private int _scrollSpeed;

    internal SpotifyWindow(WidgetRuntime runtime, int left, int top, int scrollSpeed, int sizePercent)
    {
        _runtime = runtime;
        _scrollSpeed = Math.Clamp(scrollSpeed, 0, 8);

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
            {
                runtime.OpenSettings();
                return;
            }

            if (e.GetPosition(this).Y <= 20)
                WidgetDrag.Begin(this, e, () => { runtime.SaveConfig(); runtime.SendWidgetsToDesktop(); });
        };
        Closing += (_, _) => runtime.SaveConfig();
        ContextMenu = BuildMenu(runtime);

        var root = new Grid { Width = BaseWidth, Height = BaseHeight, ClipToBounds = true };
        Content = new Viewbox { Stretch = Stretch.Fill, Child = root };

        root.Children.Add(WidgetGui.WidgetBackground(new Thickness(0), 14));

        _coverFrame = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
            Background = new SolidColorBrush(Color.FromRgb(23, 23, 23)),
            ClipToBounds = true
        };
        Canvas.SetLeft(_coverFrame, 14);
        Canvas.SetTop(_coverFrame, 18);
        var canvas = new Canvas { Width = BaseWidth, Height = BaseHeight };
        root.Children.Add(canvas);
        canvas.Children.Add(_coverFrame);

        var titleClip = new Canvas { Width = TitleWidth, Height = 27, ClipToBounds = true };
        Canvas.SetLeft(titleClip, TitleStartX);
        Canvas.SetTop(titleClip, 16);
        canvas.Children.Add(titleClip);

        _title = NewText("Spotify", 18, FontWeights.Bold, "#ffffff");
        _title.RenderTransform = _titleShift;
        titleClip.Children.Add(_title);

        _artist = NewText("Loading", 13, FontWeights.Normal, "#d4d4d4");
        _artist.Width = 210;
        _artist.TextTrimming = TextTrimming.CharacterEllipsis;
        Canvas.SetLeft(_artist, 126);
        Canvas.SetTop(_artist, 45);
        canvas.Children.Add(_artist);

        _status = NewText("Starting", 11, FontWeights.Normal, "#a3a3a3");
        _status.Width = 210;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        Canvas.SetLeft(_status, 126);
        Canvas.SetTop(_status, 68);
        canvas.Children.Add(_status);

        var prev = NewIconButton("previous", 126, "Previous");
        prev.MouseLeftButtonUp += (_, _) => RunCommand(() => _runtime.Spotify.Previous());
        canvas.Children.Add(prev);

        var playButton = NewIconButton("play", 178, "Play / Pause");
        _playIcon = (Path)((Viewbox)((Border)playButton).Child).Child;
        playButton.MouseLeftButtonUp += (_, _) => PlayOrLogin();
        canvas.Children.Add(playButton);

        var next = NewIconButton("next", 232, "Next");
        next.MouseLeftButtonUp += (_, _) => RunCommand(() => _runtime.Spotify.Next());
        canvas.Children.Add(next);

        _refreshTimer.Tick += (_, _) => Refresh();
        _marqueeTimer.Tick += (_, _) => AnimateTitle();
        ApplySize(sizePercent);
    }

    internal void Start()
    {
        _refreshTimer.Start();
        _marqueeTimer.Start();
        Refresh();
    }

    internal void ApplySettings(int scrollSpeed, int sizePercent)
    {
        _scrollSpeed = Math.Clamp(scrollSpeed, 0, 8);
        ApplySize(sizePercent);
    }

    private void ApplySize(int sizePercent)
    {
        var scale = Math.Clamp(sizePercent, 70, 150) / 100d;
        Width = BaseWidth * scale;
        Height = BaseHeight * scale;
    }

    internal void Stop()
    {
        _refreshTimer.Stop();
        _marqueeTimer.Stop();
    }

    private void PlayOrLogin()
    {
        if (!EnsureConnected())
        {
            return;
        }

        RunCommand(() => _isPlaying ? _runtime.Spotify.Pause() : _runtime.Spotify.Play());
    }

    private bool EnsureConnected()
    {
        var current = _runtime.Spotify.Current();
        if (current.Connected)
        {
            return true;
        }

        _status.Text = _runtime.Spotify.Login();
        SetPlayIcon(false);
        return false;
    }

    private void RunCommand(Func<bool> command)
    {
        if (!EnsureConnected())
        {
            return;
        }

        if (!command())
        {
            _status.Text = ShortText(_runtime.Spotify.LastError, 38);
            return;
        }

        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            Refresh();
        };
        delay.Start();
    }

    internal void Refresh()
    {
        var track = _runtime.Spotify.Current();
        if (!track.Connected)
        {
            SetTitle("Spotify");
            _artist.Text = "Not connected";
            _status.Text = ShortText(string.IsNullOrWhiteSpace(track.Error) ? "Click Login" : track.Error, 38);
            SetPlayIcon(false);
            _isPlaying = false;
            return;
        }

        if (!track.Ok || string.IsNullOrWhiteSpace(track.Name))
        {
            SetTitle("Spotify");
            _artist.Text = "No active track";
            _status.Text = track.Status == "nothing_playing" ? "Nothing playing" : track.Status;
            SetPlayIcon(false);
            _isPlaying = false;
            return;
        }

        _isPlaying = track.IsPlaying;
        SetPlayIcon(_isPlaying);
        SetTitle(track.Name);
        _artist.Text = ShortText(track.Artist, 34);
        _status.Text = _isPlaying ? "Playing" : "Paused";
        SetCover(track.AlbumImageUrl);
    }

    private void SetCover(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || string.Equals(url, _currentCoverUrl, StringComparison.Ordinal))
        {
            return;
        }

        _currentCoverUrl = url;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(url);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            _coverFrame.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        }
        catch
        {
            _coverFrame.Background = new SolidColorBrush(Color.FromRgb(23, 23, 23));
        }
    }

    private void SetTitle(string text)
    {
        _titleText = text;
        _title.Text = text;
        _titleOffset = 0;
        _titleShift.X = 0;
    }

    private void AnimateTitle()
    {
        if (_scrollSpeed <= 0 || _titleText.Length <= 24)
        {
            _titleShift.X = 0;
            return;
        }

        var overflow = Math.Max(0, (_titleText.Length * 9) - TitleWidth);
        if (overflow <= 0)
        {
            _titleShift.X = 0;
            return;
        }

        _titleOffset -= _scrollSpeed;
        if (_titleOffset < -(overflow + 28))
        {
            _titleOffset = 0;
        }

        _titleShift.X = _titleOffset;
    }

    private void SetPlayIcon(bool pause)
    {
        _playIcon.Data = Geometry.Parse(pause ? WidgetGui.IconPath("pause") : WidgetGui.IconPath("play"));
    }

    private static Border NewIconButton(string icon, double x, string tooltip)
    {
        var button = WidgetGui.IconButton(icon, 36, 28, tooltip);
        Canvas.SetLeft(button, x);
        Canvas.SetTop(button, 88);
        return button;
    }

    private static TextBlock NewText(string text, double size, FontWeight weight, string color)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = size,
            FontWeight = weight,
            Foreground = (Brush)new BrushConverter().ConvertFromString(color)!,
            TextWrapping = TextWrapping.NoWrap
        };
    }

    private static string ShortText(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Spotify";
        }

        return text.Length <= max ? text : text[..Math.Max(0, max - 3)] + "...";
    }

    private static ContextMenu BuildMenu(WidgetRuntime runtime)
    {
        var menu = new ContextMenu();
        menu.Items.Add(NewMenuItem("Refresh Spotify", (_, _) => runtime.RefreshSpotify()));
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
