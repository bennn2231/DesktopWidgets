using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WidgesDesktopDotNet;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Global\WidgesDesktopDotNet", out var ownsMutex);
        if (!ownsMutex)
        {
            return;
        }

        ExtractBundledResources();

        var app = new Application();
        using var runtime = new WidgetRuntime();
        runtime.Start();
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        app.MainWindow = runtime.ClockWindow;
        app.Run(runtime.ClockWindow);
    }

    private static void ExtractBundledResources()
    {
        const string prefix = "BundledResources/";
        var assembly = typeof(Program).Assembly;
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relativePath = resourceName[prefix.Length..]
                .Replace('/', System.IO.Path.DirectorySeparatorChar)
                .Replace('\\', System.IO.Path.DirectorySeparatorChar);
            var targetPath = System.IO.Path.Combine(AppContext.BaseDirectory, relativePath);
            if (System.IO.File.Exists(targetPath))
            {
                continue;
            }

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(targetPath)!);
            using var input = assembly.GetManifestResourceStream(resourceName);
            if (input == null)
            {
                continue;
            }

            using var output = System.IO.File.Create(targetPath);
            input.CopyTo(output);
        }
    }
}

internal sealed class WidgetRuntime : IDisposable
{
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(12) };

    private readonly string _skinRoot;
    private readonly string _configPath;
    private readonly string _iconsPath;

    private WidgetConfig _config;
    private readonly List<ForecastRowUi> _forecastRows = [];
    private readonly LuaWidgetManager _luaWidgetManager;

    internal ClockWindow ClockWindow { get; }
    internal WeatherWindow WeatherWindow { get; }
    internal SpotifyService Spotify { get; }
    internal SpotifyWindow SpotifyWindow { get; }

    internal WidgetRuntime()
    {
        _skinRoot = FindSkinRoot();
        _configPath = System.IO.Path.Combine(_skinRoot, "DesktopWidget", "config.json");
        _iconsPath = System.IO.Path.Combine(_skinRoot, "@Resources", "WeatherIcons");
        _config = WidgetConfig.Load(_configPath);

        ClockWindow = new ClockWindow(this, _config.ClockLeft, _config.ClockTop, _config.ClockSizePercent, _config.ClockBackgroundEnabled);
        WeatherWindow = new WeatherWindow(this, _config.WeatherLeft, _config.WeatherTop, _config.WeatherSizePercent, _config.WeatherBackgroundEnabled);
        Spotify = new SpotifyService(_skinRoot);
        if (string.IsNullOrWhiteSpace(_config.SpotifyClientId) && !string.IsNullOrWhiteSpace(Spotify.ClientId))
        {
            _config.SpotifyClientId = Spotify.ClientId;
        }

        if (string.IsNullOrWhiteSpace(_config.SpotifyRedirectUri) && !string.IsNullOrWhiteSpace(Spotify.RedirectUri))
        {
            _config.SpotifyRedirectUri = Spotify.RedirectUri;
        }

        if (!string.IsNullOrWhiteSpace(_config.SpotifyClientId))
        {
            Spotify.Configure(_config.SpotifyClientId, _config.SpotifyRedirectUri);
        }

        SpotifyWindow = new SpotifyWindow(this, _config.SpotifyLeft, _config.SpotifyTop, _config.SpotifyTitleScrollSpeed, _config.SpotifySizePercent);
        _luaWidgetManager = new LuaWidgetManager(this, _skinRoot);

        _clockTimer.Tick += (_, _) => UpdateClock();
        _weatherTimer.Tick += async (_, _) => await UpdateWeatherAsync();
        WeatherWindow.ContentRendered += async (_, _) => await UpdateWeatherAsync();
    }

    internal void Start()
    {
        ApplyColors();
        UpdateClock();
        _clockTimer.Start();
        _weatherTimer.Start();
        WeatherWindow.Show();
        if (_config.SpotifyEnabled)
        {
            SpotifyWindow.Show();
            SpotifyWindow.Start();
        }

        _luaWidgetManager.LoadWidgets(_config);

        DesktopShell.AttachRestartHook(ClockWindow, SendWidgetsToDesktop);
        SendWidgetsToDesktop();
        SaveConfig();
    }

    internal void OpenSettings()
    {
        var settings = new SettingsWindow(this, _config);
        if (settings.ShowDialog() != true)
        {
            return;
        }

        _config = settings.Value;
        ApplyColors();
        ApplySizes();
        ApplyBackgrounds();
        ApplySpotifySettings();
        SaveConfig();
        UpdateClock();
        _ = UpdateWeatherAsync();
        SpotifyWindow.Refresh();
        _luaWidgetManager.Reload(_config);
        SendWidgetsToDesktop();
    }

    internal void OpenLuaEditor()
    {
        OpenSettings();
    }

    internal void RefreshWeather() => _ = UpdateWeatherAsync();

    internal void RefreshSpotify() => SpotifyWindow.Refresh();

    internal void UpdateSpotifySettings(bool enabled, string clientId, string redirectUri, int titleScrollSpeed, int sizePercent)
    {
        _config.SpotifyEnabled = enabled;
        _config.SpotifyClientId = clientId.Trim();
        _config.SpotifyRedirectUri = string.IsNullOrWhiteSpace(redirectUri)
            ? "http://127.0.0.1:8765/callback/"
            : redirectUri.Trim();
        _config.SpotifyTitleScrollSpeed = Math.Clamp(titleScrollSpeed, 0, 8);
        _config.SpotifySizePercent = Math.Clamp(sizePercent, 70, 150);
        ApplySpotifySettings();
        SaveConfig();
        SpotifyWindow.Refresh();
        SendWidgetsToDesktop();
    }

    internal void ReloadLuaWidgets()
    {
        SaveConfig();
        _luaWidgetManager.Reload(_config);
        SendWidgetsToDesktop();
    }

    internal void LoadLuaWidgets()
    {
        _config.LuaWidgetsEnabled = true;
        SaveConfig();
        _luaWidgetManager.Reload(_config);
        SendWidgetsToDesktop();
    }

    internal void SendWidgetsToDesktop()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ClockWindow.Topmost = false;
            WeatherWindow.Topmost = false;
            SpotifyWindow.Topmost = false;
            DesktopShell.Embed(ClockWindow);
            DesktopShell.Embed(WeatherWindow);
            DesktopShell.Embed(SpotifyWindow);
            _luaWidgetManager.EmbedAll();
        };
        timer.Start();
    }

    internal string LuaWidgetsDirectory => ResolveConfiguredPath(_config.LuaWidgetsPath);

    internal string ResolveLuaWidgetsPath(string path) => ResolveConfiguredPath(path);

    internal FontFamily ResolveFontFamily(string fontFamily)
    {
        var trimmed = string.IsNullOrWhiteSpace(fontFamily) ? "Segoe UI" : fontFamily.Trim();
        var fontsDirectory = System.IO.Path.Combine(_skinRoot, "@Resources", "Fonts");
        if (System.IO.Directory.Exists(fontsDirectory))
        {
            try
            {
                var baseUri = new Uri(fontsDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar);
                return new FontFamily(baseUri, $"./#{trimmed}");
            }
            catch
            {
                // Fall through to installed Windows font lookup.
            }
        }

        try
        {
            return new FontFamily(trimmed);
        }
        catch
        {
            return new FontFamily("Segoe UI");
        }
    }

    internal void SaveConfig()
    {
        (_config.ClockLeft, _config.ClockTop) = DesktopShell.GetScreenPosition(ClockWindow);
        (_config.WeatherLeft, _config.WeatherTop) = DesktopShell.GetScreenPosition(WeatherWindow);
        (_config.SpotifyLeft, _config.SpotifyTop) = DesktopShell.GetScreenPosition(SpotifyWindow);
        _luaWidgetManager.SavePositions(_config);
        _config.Save(_configPath);
    }

    internal string ResolveIconPath(int iconId) => System.IO.Path.Combine(_iconsPath, $"{iconId}.png");

    internal Brush MainTextBrush => NewBrush(_config.TextColor, 255);
    internal Brush SoftTextBrush => NewBrush(_config.TextColor, 205);
    internal Brush BarTrackBrush => NewBrush(_config.TextColor, 60);
    internal Brush BarFillBrush => NewBrush(_config.TextColor, 220);
    internal Brush DividerBrush => NewBrush(_config.TextColor, 130);

    internal void RegisterForecastRows(IEnumerable<ForecastRowUi> rows)
    {
        _forecastRows.Clear();
        _forecastRows.AddRange(rows);
    }

    private void ApplyColors()
    {
        ClockWindow.TimeText.Foreground = MainTextBrush;
        ClockWindow.SecondsText.Foreground = MainTextBrush;
        ClockWindow.AmPmText.Foreground = MainTextBrush;
        ClockWindow.DateText.Foreground = SoftTextBrush;

        WeatherWindow.TempText.Foreground = MainTextBrush;
        WeatherWindow.CityText.Foreground = SoftTextBrush;
        WeatherWindow.ConditionText.Foreground = MainTextBrush;
        WeatherWindow.Separator.Background = DividerBrush;
        WeatherWindow.CurrentIcon.Background = MainTextBrush;

        foreach (var row in _forecastRows)
        {
            row.Day.Foreground = MainTextBrush;
            row.Min.Foreground = MainTextBrush;
            row.Max.Foreground = MainTextBrush;
            row.Icon.Background = MainTextBrush;
            row.Track.Background = BarTrackBrush;
            row.Fill.Background = BarFillBrush;
        }
    }

    private void ApplySizes()
    {
        ClockWindow.ApplySize(_config.ClockSizePercent);
        WeatherWindow.ApplySize(_config.WeatherSizePercent);
    }

    private void ApplyBackgrounds()
    {
        ClockWindow.SetBackgroundEnabled(_config.ClockBackgroundEnabled);
        WeatherWindow.SetBackgroundEnabled(_config.WeatherBackgroundEnabled);
    }

    private void ApplySpotifySettings()
    {
        SpotifyWindow.ApplySettings(_config.SpotifyTitleScrollSpeed, _config.SpotifySizePercent);
        if (!string.IsNullOrWhiteSpace(_config.SpotifyClientId))
        {
            Spotify.Configure(_config.SpotifyClientId, _config.SpotifyRedirectUri);
        }

        if (_config.SpotifyEnabled)
        {
            if (!SpotifyWindow.IsVisible)
            {
                SpotifyWindow.Show();
            }

            SpotifyWindow.Start();
        }
        else
        {
            SpotifyWindow.Stop();
            SpotifyWindow.Hide();
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        if (WidgetConfig.Is24HourFormat(_config.Format))
        {
            ClockWindow.TimeText.Text = now.ToString("H:mm", CultureInfo.InvariantCulture);
            ClockWindow.AmPmText.Text = string.Empty;
        }
        else
        {
            ClockWindow.TimeText.Text = now.ToString("h:mm", CultureInfo.InvariantCulture);
            ClockWindow.AmPmText.Text = now.ToString("tt", CultureInfo.InvariantCulture).ToUpperInvariant();
        }

        ClockWindow.SecondsText.Text = now.ToString("ss", CultureInfo.InvariantCulture);
        ClockWindow.SecondsText.Visibility = _config.ClockSecondsEnabled ? Visibility.Visible : Visibility.Collapsed;
        ClockWindow.DateText.Text = now.ToString("dddd, MMMM dd", CultureInfo.InvariantCulture);
    }

    private async Task UpdateWeatherAsync()
    {
        WeatherWindow.CityText.Text = _config.Location.ToUpperInvariant();
        WeatherWindow.ConditionText.Text = "Fetching forecast";
        WeatherWindow.TempText.Text = "--\u00B0";
        WeatherWindow.CurrentIcon.OpacityMask = NewIconBrush(26);

        try
        {
            var place = await GeocodeAsync(_config.Location);
            if (place == null)
            {
                WeatherWindow.ConditionText.Text = "Location not found";
                return;
            }

            var forecast = await GetForecastAsync(place.Latitude, place.Longitude, _config.Unit);
            if (forecast == null)
            {
                WeatherWindow.ConditionText.Text = "Forecast unavailable";
                return;
            }

            var currentMap = MapWeatherCode(forecast.CurrentCode, forecast.IsDay);
            WeatherWindow.CurrentIcon.OpacityMask = NewIconBrush(currentMap.IconId);
            WeatherWindow.TempText.Text = $"{Math.Round(forecast.CurrentTemp):0}\u00B0";
            WeatherWindow.CityText.Text = place.Name.ToUpperInvariant();
            WeatherWindow.ConditionText.Text = currentMap.Text;

            var displayMins = forecast.Mins.Select(v => (int)Math.Round(v)).ToArray();
            var displayMaxes = forecast.Maxes.Select(v => (int)Math.Round(v)).ToArray();
            var weekMin = displayMins.Min();
            var weekMax = displayMaxes.Max();
            var range = weekMax - weekMin;

            var trackWidth = WeatherWindow.TrackWidth;
            for (var i = 0; i < 7; i++)
            {
                var min = displayMins[i];
                var max = displayMaxes[i];
                if (max < min)
                {
                    (min, max) = (max, min);
                }

                var row = _forecastRows[i];
                row.Day.Text = DateTime.Parse(forecast.Dates[i], CultureInfo.InvariantCulture).ToString("ddd", CultureInfo.InvariantCulture);
                row.Min.Text = $"{min}\u00B0";
                row.Max.Text = $"{max}\u00B0";
                row.Icon.OpacityMask = NewIconBrush(MapWeatherCode(forecast.DayCodes[i], 1).IconId);

                if (range <= 0)
                {
                    Canvas.SetLeft(row.Fill, 0);
                    row.Fill.Width = 0;
                    continue;
                }

                var lowRatio = Math.Clamp((min - weekMin) / (double)range, 0d, 1d);
                var highRatio = Math.Clamp((max - weekMin) / (double)range, 0d, 1d);
                var left = Math.Floor((lowRatio * trackWidth) + 0.5);
                var right = Math.Floor((highRatio * trackWidth) + 0.5);
                var width = Math.Max(right - left, 2d);
                left = Math.Clamp(left, 0, Math.Max(0, trackWidth - 2));
                if (left + width > trackWidth)
                {
                    width = Math.Max(0, trackWidth - left);
                }

                Canvas.SetLeft(row.Fill, left);
                row.Fill.Width = width;
            }
        }
        catch
        {
            WeatherWindow.ConditionText.Text = "Forecast unavailable";
        }
    }

    private async Task<GeoPlace?> GeocodeAsync(string location)
    {
        var query = Uri.EscapeDataString(location);
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={query}&count=1&language=en&format=json";
        using var stream = await _httpClient.GetStreamAsync(url);
        using var doc = await JsonDocument.ParseAsync(stream);
        var results = doc.RootElement.GetProperty("results");
        if (results.GetArrayLength() == 0)
        {
            return null;
        }

        var first = results[0];
        return new GeoPlace(
            first.GetProperty("name").GetString() ?? location,
            first.GetProperty("latitude").GetDouble(),
            first.GetProperty("longitude").GetDouble()
        );
    }

    private async Task<WeatherPayload?> GetForecastAsync(double latitude, double longitude, string unit)
    {
        var tempUnit = string.Equals(unit, "c", StringComparison.OrdinalIgnoreCase) ? "celsius" : "fahrenheit";
        var url =
            $"https://api.open-meteo.com/v1/forecast?latitude={latitude.ToString(CultureInfo.InvariantCulture)}&longitude={longitude.ToString(CultureInfo.InvariantCulture)}&current=temperature_2m,is_day,weather_code&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=7&temperature_unit={tempUnit}";
        using var stream = await _httpClient.GetStreamAsync(url);
        using var doc = await JsonDocument.ParseAsync(stream);

        var current = doc.RootElement.GetProperty("current");
        var daily = doc.RootElement.GetProperty("daily");

        return new WeatherPayload(
            current.GetProperty("temperature_2m").GetDouble(),
            current.GetProperty("is_day").GetInt32(),
            current.GetProperty("weather_code").GetInt32(),
            daily.GetProperty("time").EnumerateArray().Select(x => x.GetString() ?? string.Empty).Take(7).ToArray(),
            daily.GetProperty("weather_code").EnumerateArray().Select(x => x.GetInt32()).Take(7).ToArray(),
            daily.GetProperty("temperature_2m_min").EnumerateArray().Select(x => x.GetDouble()).Take(7).ToArray(),
            daily.GetProperty("temperature_2m_max").EnumerateArray().Select(x => x.GetDouble()).Take(7).ToArray()
        );
    }

    private static (int IconId, string Text) MapWeatherCode(int code, int isDay)
    {
        return code switch
        {
            0 => isDay == 0 ? (31, "Clear") : (32, "Clear"),
            1 => isDay == 0 ? (33, "Mostly clear") : (34, "Mostly clear"),
            2 => isDay == 0 ? (29, "Partly cloudy") : (30, "Partly cloudy"),
            3 => (26, "Cloudy"),
            45 or 48 => (20, "Fog"),
            51 or 53 or 55 or 56 or 57 => (11, "Drizzle"),
            61 or 63 or 65 or 66 or 67 => (12, "Rain"),
            71 or 73 or 75 or 77 => (16, "Snow"),
            80 or 81 or 82 => (11, "Showers"),
            85 or 86 => (16, "Snow showers"),
            95 or 96 or 99 => (4, "Thunderstorm"),
            _ => (26, "Weather")
        };
    }

    private Brush NewIconBrush(int iconId)
    {
        var path = ResolveIconPath(iconId);
        if (!System.IO.File.Exists(path))
        {
            return NewFallbackIconBrush(iconId);
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            return new ImageBrush(image) { Stretch = Stretch.Uniform };
        }
        catch
        {
            return NewFallbackIconBrush(iconId);
        }
    }

    private static Brush NewFallbackIconBrush(int iconId)
    {
        var drawing = new DrawingGroup();
        switch (iconId)
        {
            case 31:
            case 32:
            case 33:
            case 34:
                AddSun(drawing);
                break;
            case 11:
            case 12:
                AddCloud(drawing);
                AddRain(drawing);
                break;
            case 16:
                AddCloud(drawing);
                AddSnow(drawing);
                break;
            case 20:
                AddFog(drawing);
                break;
            case 4:
                AddCloud(drawing);
                AddLightning(drawing);
                break;
            default:
                AddCloud(drawing);
                break;
        }

        drawing.Freeze();
        return new DrawingBrush(drawing)
        {
            Stretch = Stretch.Uniform,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center
        };
    }

    private static void AddSun(DrawingGroup drawing)
    {
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, new EllipseGeometry(new Point(50, 50), 20, 20)));
        for (var i = 0; i < 8; i++)
        {
            var angle = Math.PI * 2 * i / 8;
            var x = Math.Cos(angle);
            var y = Math.Sin(angle);
            drawing.Children.Add(new GeometryDrawing(
                Brushes.White,
                null,
                new RectangleGeometry(new Rect(47 + (x * 32), 47 + (y * 32), 6, 6), 3, 3)));
        }
    }

    private static void AddCloud(DrawingGroup drawing)
    {
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, new EllipseGeometry(new Point(38, 54), 20, 15)));
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, new EllipseGeometry(new Point(56, 45), 23, 20)));
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, new EllipseGeometry(new Point(72, 58), 18, 14)));
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(24, 54, 62, 22), 11, 11)));
    }

    private static void AddRain(DrawingGroup drawing)
    {
        foreach (var x in new[] { 36d, 52d, 68d })
        {
            drawing.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(x, 80, 5, 16), 2.5, 2.5)));
        }
    }

    private static void AddSnow(DrawingGroup drawing)
    {
        foreach (var x in new[] { 36d, 52d, 68d })
        {
            drawing.Children.Add(new GeometryDrawing(Brushes.White, null, new EllipseGeometry(new Point(x + 2.5, 88), 4, 4)));
        }
    }

    private static void AddFog(DrawingGroup drawing)
    {
        foreach (var y in new[] { 38d, 52d, 66d })
        {
            drawing.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(22, y, 64, 7), 3.5, 3.5)));
        }
    }

    private static void AddLightning(DrawingGroup drawing)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(53, 70), true, true);
            ctx.LineTo(new Point(43, 96), true, false);
            ctx.LineTo(new Point(59, 91), true, false);
            ctx.LineTo(new Point(51, 112), true, false);
            ctx.LineTo(new Point(75, 80), true, false);
            ctx.LineTo(new Point(60, 84), true, false);
        }

        geometry.Freeze();
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, geometry));
    }

    private static SolidColorBrush NewBrush(string rgb, byte alpha)
    {
        var parts = rgb.Split(',', StringSplitOptions.TrimEntries);
        var r = byte.Parse(parts[0], CultureInfo.InvariantCulture);
        var g = byte.Parse(parts[1], CultureInfo.InvariantCulture);
        var b = byte.Parse(parts[2], CultureInfo.InvariantCulture);
        return new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
    }

    private static string FindSkinRoot()
    {
        var current = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current != null; i++)
        {
            if (System.IO.Directory.Exists(System.IO.Path.Combine(current.FullName, "@Resources")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return System.IO.Directory.GetCurrentDirectory();
    }

    private string ResolveConfiguredPath(string path)
    {
        var trimmed = string.IsNullOrWhiteSpace(path) ? @"Widger\Lua" : path.Trim();
        if (System.IO.Path.IsPathRooted(trimmed))
        {
            return trimmed;
        }

        return System.IO.Path.GetFullPath(System.IO.Path.Combine(_skinRoot, trimmed));
    }

    public void Dispose()
    {
        _clockTimer.Stop();
        _weatherTimer.Stop();
        SpotifyWindow.Stop();
        _httpClient.Dispose();
        SaveConfig();
        SpotifyWindow.Close();
        _luaWidgetManager.Dispose();
        Spotify.Dispose();
    }
}

internal sealed record GeoPlace(string Name, double Latitude, double Longitude);

internal sealed record WeatherPayload(
    double CurrentTemp,
    int IsDay,
    int CurrentCode,
    string[] Dates,
    int[] DayCodes,
    double[] Mins,
    double[] Maxes
);


internal sealed class WidgetConfig
{
    public string Location { get; set; } = "fort collins";
    public string Unit { get; set; } = "f";
    public string Format { get; set; } = "h";
    public string TextColor { get; set; } = "255,255,255";
    public bool ClockSecondsEnabled { get; set; } = true;
    public bool ClockBackgroundEnabled { get; set; }
    public int ClockSizePercent { get; set; } = 100;
    public int WeatherSizePercent { get; set; } = 100;
    public bool WeatherBackgroundEnabled { get; set; }
    public int ClockLeft { get; set; } = 70;
    public int ClockTop { get; set; } = 20;
    public int WeatherLeft { get; set; } = 70;
    public int WeatherTop { get; set; } = 220;
    public bool SpotifyEnabled { get; set; } = true;
    public int SpotifyLeft { get; set; } = 80;
    public int SpotifyTop { get; set; } = 700;
    public int SpotifySizePercent { get; set; } = 100;
    public string SpotifyClientId { get; set; } = string.Empty;
    public string SpotifyRedirectUri { get; set; } = "http://127.0.0.1:8765/callback/";
    public int SpotifyTitleScrollSpeed { get; set; } = 3;
    public bool LuaWidgetsEnabled { get; set; } = true;
    public string LuaWidgetsPath { get; set; } = @"Widger\Lua";
    public Dictionary<string, LuaWidgetState> LuaWidgets { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>> LuaWidgetSettings { get; set; } = [];

    internal WidgetConfig Clone()
    {
        var clone = (WidgetConfig)MemberwiseClone();
        clone.LuaWidgets = LuaWidgets.ToDictionary(
            pair => pair.Key,
            pair => new LuaWidgetState
            {
                Left = pair.Value.Left,
                Top = pair.Value.Top,
                Width = pair.Value.Width,
                Height = pair.Value.Height
            },
            StringComparer.OrdinalIgnoreCase);
        clone.LuaWidgetSettings = LuaWidgetSettings.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToDictionary(setting => setting.Key, setting => setting.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        return clone;
    }

    internal static WidgetConfig Load(string path)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        if (!System.IO.File.Exists(path))
        {
            return new WidgetConfig();
        }

        try
        {
            var cfg = JsonSerializer.Deserialize<WidgetConfig>(System.IO.File.ReadAllText(path), options);
            if (cfg == null)
            {
                return new WidgetConfig();
            }

            cfg.Format = Is24HourFormat(cfg.Format) ? "H" : "h";
            if (string.IsNullOrWhiteSpace(cfg.LuaWidgetsPath))
            {
                cfg.LuaWidgetsPath = @"Widger\Lua";
            }

            cfg.LuaWidgets ??= [];
            cfg.LuaWidgetSettings ??= [];
            if (string.IsNullOrWhiteSpace(cfg.SpotifyRedirectUri))
            {
                cfg.SpotifyRedirectUri = "http://127.0.0.1:8765/callback/";
            }

            cfg.SpotifyTitleScrollSpeed = Math.Clamp(cfg.SpotifyTitleScrollSpeed, 0, 8);
            cfg.SpotifySizePercent = Math.Clamp(cfg.SpotifySizePercent, 70, 150);

            return cfg;
        }
        catch
        {
            return new WidgetConfig();
        }
    }

    internal void Save(string path)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        System.IO.File.WriteAllText(path, json);
    }

    internal static bool IsRgb(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var n) || n < 0 || n > 255)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool Is24HourFormat(string value)
    {
        var trimmed = value.Trim();
        if (trimmed == "H")
        {
            return true;
        }

        if (trimmed == "h")
        {
            return false;
        }

        var normalized = trimmed.ToLowerInvariant();
        return normalized is "24" or "24-hour" or "24 hour" or "24h";
    }
}

internal sealed class LuaWidgetState
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
