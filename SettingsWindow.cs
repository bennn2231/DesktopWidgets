using System.Text.Json;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using MoonSharp.Interpreter;

namespace WidgesDesktopDotNet;

internal sealed class SettingsWindow : Window
{
    private static readonly FontFamily UiFont = new("Segoe UI");
    private static readonly FontFamily CodeFont = new("Consolas");

    private readonly WidgetRuntime _runtime;
    private readonly TextBox _location;
    private readonly ComboBox _unit;
    private readonly ComboBox _format;
    private readonly ComboBox _clockSeconds;
    private readonly ComboBox _clockBackground;
    private readonly TextBox _textColor;
    private readonly ComboBox _luaEnabled;
    private readonly TextBox _luaWidgetsPath;
    private readonly ComboBox _luaWidgetList;
    private readonly TextBox _luaEditor;
    private readonly TextBlock _luaEditorStatus;
    private readonly TextBlock _luaEditorPath;
    private readonly StackPanel _luaSettingsHost;
    private readonly List<LuaSettingBinding> _luaSettingBindings = [];
    private readonly Slider _clockSize;
    private readonly Slider _weatherSize;
    private readonly ComboBox _spotifyEnabled;
    private readonly TextBox _spotifyClientId;
    private readonly TextBox _spotifyRedirectUri;
    private readonly Slider _spotifySize;
    private readonly Slider _spotifyTitleScrollSpeed;
    private readonly ComboBox _weatherBackground;
    private readonly Border _shell;
    private readonly WidgetConfig _value;

    internal WidgetConfig Value => _value;

    internal SettingsWindow(WidgetRuntime runtime, WidgetConfig source)
    {
        _runtime = runtime;
        _value = source.Clone();
        Width = 700;
        Height = 560;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = "Widges Settings";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SourceInitialized += (_, _) =>
        {
            NativeWindowEffects.ApplyRoundedRegion(this, 28);
        };
        SizeChanged += (_, _) =>
        {
            NativeWindowEffects.ApplyRoundedRegion(this, 28);
        };

        _shell = BuildShell();
        _shell.Loaded += (_, _) => ApplyRoundedClip(_shell, 14);
        _shell.SizeChanged += (_, _) =>
        {
            ApplyRoundedClip(_shell, 14);
        };
        Content = _shell;

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ((Grid)_shell.Child).Children.Add(layout);

        var main = new Grid { Margin = new Thickness(18, 16, 18, 16) };
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(main, 1);
        layout.Children.Add(main);

        var titleBar = BuildTitleBar();
        Grid.SetRow(titleBar, 0);
        main.Children.Add(titleBar);

        var content = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };

        _format = FormatDropdown(_value.Format);
        _clockSeconds = EnabledDropdown(_value.ClockSecondsEnabled);
        _clockBackground = EnabledDropdown(_value.ClockBackgroundEnabled);
        _clockSize = SliderControl(_value.ClockSizePercent);
        var clockSection = Section(
            "Clock / Date",
            "Time display",
            Field("Clock format", _format),
            Field("Seconds counter", _clockSeconds),
            Field("Background", _clockBackground),
            SliderField("Clock and date size", _clockSize)
        );
        content.Children.Add(clockSection);

        _location = TextBoxControl(_value.Location);
        _unit = UnitDropdown(_value.Unit);
        _weatherBackground = EnabledDropdown(_value.WeatherBackgroundEnabled);
        _weatherSize = SliderControl(_value.WeatherSizePercent);
        var weatherSection = Section(
            "Weather",
            "Forecast display",
            Field("City / place", _location),
            Field("Temperature unit", _unit),
            Field("Background", _weatherBackground),
            SliderField("Weather size", _weatherSize)
        );
        content.Children.Add(weatherSection);

        _spotifyEnabled = EnabledDropdown(_value.SpotifyEnabled);
        _spotifyClientId = TextBoxControl(_value.SpotifyClientId);
        _spotifyRedirectUri = TextBoxControl(_value.SpotifyRedirectUri);
        _spotifySize = SliderControl(_value.SpotifySizePercent);
        _spotifyTitleScrollSpeed = SliderControl(_value.SpotifyTitleScrollSpeed, 0, 8, 1);
        var spotifySection = Section(
            "Spotify",
            "Hardcoded controller widget",
            Field("Spotify widget", _spotifyEnabled),
            Field("Client ID", _spotifyClientId),
            Field("Redirect URI", _spotifyRedirectUri),
            SliderField("Spotify size", _spotifySize),
            LuaSliderField("Title scroll speed", _spotifyTitleScrollSpeed)
        );
        content.Children.Add(spotifySection);
        HookSpotifyLiveSettings();

        _textColor = TextBoxControl(_value.TextColor);
        var appearanceSection = Section(
            "Appearance",
            "Shared widget color",
            Field("Text color (r,g,b)", _textColor)
        );
        content.Children.Add(appearanceSection);

        _luaEnabled = EnabledDropdown(_value.LuaWidgetsEnabled);
        _luaWidgetsPath = TextBoxControl(_value.LuaWidgetsPath);
        var luaBrowse = SecondaryButton("Browse");
        luaBrowse.Width = 82;
        luaBrowse.Click += (_, _) => BrowseForLuaFolder();
        _luaWidgetList = ComboBoxControl();
        _luaWidgetList.SelectionChanged += (_, _) => OpenSelectedLuaScript();
        _luaEditor = LuaEditorControl();
        _luaEditorStatus = new TextBlock
        {
            FontFamily = UiFont,
            FontSize = 12,
            Foreground = MutedTextBrush(),
            Text = "No Lua widget selected.",
            VerticalAlignment = VerticalAlignment.Center
        };
        _luaEditorPath = new TextBlock
        {
            FontFamily = UiFont,
            FontSize = 11.5,
            Foreground = MutedTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 0)
        };
        _luaSettingsHost = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var luaSection = Section(
            "Lua Widgets",
            "Custom scripted widgets",
            Field("Lua scripting", _luaEnabled),
            FolderField("Load from", _luaWidgetsPath, luaBrowse),
            BuildLuaEditor()
        );
        content.Children.Add(luaSection);
        RefreshLuaWidgetList();

        var scroller = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            PanningMode = PanningMode.VerticalOnly
        };
        scroller.Resources.Add(typeof(ScrollBar), RoundedScrollBarStyle());
        Grid.SetRow(scroller, 1);
        main.Children.Add(scroller);

        var sidebar = BuildSidebar(
            () => clockSection.BringIntoView(),
            () => weatherSection.BringIntoView(),
            () => spotifySection.BringIntoView(),
            () => appearanceSection.BringIntoView(),
            () => luaSection.BringIntoView()
        );
        Grid.SetColumn(sidebar, 0);
        layout.Children.Add(sidebar);

        var actions = BuildActions();
        Grid.SetRow(actions, 2);
        main.Children.Add(actions);
    }

    private Border BuildShell()
    {
        var shell = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new LinearGradientBrush(
                Color.FromRgb(43, 45, 49),
                Color.FromRgb(24, 25, 29),
                90),
            BorderBrush = new SolidColorBrush(Color.FromArgb(88, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Effect = new DropShadowEffect
            {
                BlurRadius = 28,
                ShadowDepth = 0,
                Opacity = 0.42,
                Color = Color.FromRgb(0, 0, 0)
            }
        };
        shell.Child = new Grid();
        return shell;
    }

    private Grid BuildSidebar(Action showClock, Action showWeather, Action showSpotify, Action showAppearance, Action showLua)
    {
        var sidebar = new Grid
        {
            Background = PanelBrush(),
            Margin = new Thickness(1),
            ClipToBounds = true
        };
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var brand = new StackPanel { Margin = new Thickness(18, 18, 18, 18) };
        brand.Children.Add(new TextBlock
        {
            Text = "Widges",
            FontFamily = UiFont,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = PrimaryTextBrush()
        });
        brand.Children.Add(new TextBlock
        {
            Text = "Widget settings",
            FontFamily = UiFont,
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = MutedTextBrush()
        });
        sidebar.Children.Add(brand);

        var nav = new StackPanel { Margin = new Thickness(14, 4, 14, 0) };
        var items = new List<Button>();
        var clock = NavItem("Clock / Date", showClock);
        var weather = NavItem("Weather", showWeather);
        var spotify = NavItem("Spotify", showSpotify);
        var appearance = NavItem("Appearance", showAppearance);
        var lua = NavItem("Lua Widgets", showLua);
        items.Add(clock);
        items.Add(weather);
        items.Add(spotify);
        items.Add(appearance);
        items.Add(lua);
        foreach (var item in items)
        {
            item.Click += (_, _) => SetActiveNavItem(items, item);
            nav.Children.Add(item);
        }
        SetActiveNavItem(items, clock);
        Grid.SetRow(nav, 1);
        sidebar.Children.Add(nav);

        return sidebar;
    }

    private Grid BuildTitleBar()
    {
        var titleBar = new Grid();
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = "Settings",
            FontFamily = UiFont,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = PrimaryTextBrush()
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Tune each desktop widget",
            FontFamily = UiFont,
            FontSize = 12.5,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = MutedTextBrush()
        });
        titleBar.Children.Add(heading);

        var close = GhostButton("X", 36);
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        titleBar.Children.Add(close);

        return titleBar;
    }

    private StackPanel BuildActions()
    {
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var cancel = SecondaryButton("Cancel");
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => Close();

        var save = PrimaryButton("Save");
        save.Click += (_, _) =>
        {
            _value.Location = string.IsNullOrWhiteSpace(_location.Text) ? _value.Location : _location.Text.Trim();
            _value.Unit = _unit.SelectedIndex == 1 ? "c" : "f";
            _value.Format = _format.SelectedIndex == 1 ? "H" : "h";
            _value.ClockSecondsEnabled = _clockSeconds.SelectedIndex == 0;
            _value.ClockBackgroundEnabled = _clockBackground.SelectedIndex == 0;
            _value.ClockSizePercent = (int)Math.Round(_clockSize.Value);
            _value.WeatherSizePercent = (int)Math.Round(_weatherSize.Value);
            _value.WeatherBackgroundEnabled = _weatherBackground.SelectedIndex == 0;
            _value.SpotifyEnabled = _spotifyEnabled.SelectedIndex == 0;
            _value.SpotifyClientId = _spotifyClientId.Text.Trim();
            _value.SpotifyRedirectUri = string.IsNullOrWhiteSpace(_spotifyRedirectUri.Text)
                ? "http://127.0.0.1:8765/callback/"
                : _spotifyRedirectUri.Text.Trim();
            _value.SpotifySizePercent = (int)Math.Round(_spotifySize.Value);
            _value.SpotifyTitleScrollSpeed = (int)Math.Round(_spotifyTitleScrollSpeed.Value);
            _value.LuaWidgetsEnabled = _luaEnabled.SelectedIndex == 0;
            _value.LuaWidgetsPath = string.IsNullOrWhiteSpace(_luaWidgetsPath.Text)
                ? @"Widger\Lua"
                : _luaWidgetsPath.Text.Trim();
            if (WidgetConfig.IsRgb(_textColor.Text.Trim()))
            {
                _value.TextColor = _textColor.Text.Trim();
            }

            DialogResult = true;
            Close();
        };

        actions.Children.Add(cancel);
        actions.Children.Add(save);
        return actions;
    }

    private void HookSpotifyLiveSettings()
    {
        _spotifyEnabled.SelectionChanged += (_, _) => ApplySpotifyLiveSettings();
        _spotifyClientId.TextChanged += (_, _) => ApplySpotifyLiveSettings();
        _spotifyRedirectUri.TextChanged += (_, _) => ApplySpotifyLiveSettings();
        _spotifySize.ValueChanged += (_, _) => ApplySpotifyLiveSettings();
        _spotifyTitleScrollSpeed.ValueChanged += (_, _) => ApplySpotifyLiveSettings();
    }

    private void ApplySpotifyLiveSettings()
    {
        if (!IsLoaded)
        {
            return;
        }

        _value.SpotifyEnabled = _spotifyEnabled.SelectedIndex == 0;
        _value.SpotifyClientId = _spotifyClientId.Text.Trim();
        _value.SpotifyRedirectUri = string.IsNullOrWhiteSpace(_spotifyRedirectUri.Text)
            ? "http://127.0.0.1:8765/callback/"
            : _spotifyRedirectUri.Text.Trim();
        _value.SpotifySizePercent = (int)Math.Round(_spotifySize.Value);
        _value.SpotifyTitleScrollSpeed = (int)Math.Round(_spotifyTitleScrollSpeed.Value);

        _runtime.UpdateSpotifySettings(
            _value.SpotifyEnabled,
            _value.SpotifyClientId,
            _value.SpotifyRedirectUri,
            _value.SpotifyTitleScrollSpeed,
            _value.SpotifySizePercent);
    }

    private void BrowseForLuaFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose Lua widgets folder",
            InitialDirectory = System.IO.Path.IsPathRooted(_luaWidgetsPath.Text)
                ? _luaWidgetsPath.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog(this) == true)
        {
            _luaWidgetsPath.Text = dialog.FolderName;
            RefreshLuaWidgetList();
        }
    }

    private UIElement BuildLuaEditor()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };

        var toolbar = new Grid { Margin = new Thickness(0, 2, 0, 8) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(138) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        toolbar.Children.Add(new TextBlock
        {
            Text = "Script editor",
            FontFamily = UiFont,
            FontSize = 12.5,
            Foreground = SoftTextBrush(),
            VerticalAlignment = VerticalAlignment.Center
        });

        Grid.SetColumn(_luaWidgetList, 1);
        toolbar.Children.Add(_luaWidgetList);

        var refresh = SecondaryButton("Refresh");
        refresh.Width = 82;
        refresh.Margin = new Thickness(8, 0, 0, 0);
        refresh.Click += (_, _) => RefreshLuaWidgetList();
        Grid.SetColumn(refresh, 2);
        toolbar.Children.Add(refresh);

        stack.Children.Add(toolbar);
        stack.Children.Add(_luaEditor);

        var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_luaEditorStatus);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);

        var save = SecondaryButton("Save");
        save.Width = 68;
        save.Click += (_, _) => SaveLuaScript();
        buttons.Children.Add(save);

        var load = SecondaryButton("Load");
        load.Width = 68;
        load.Margin = new Thickness(8, 0, 0, 0);
        load.Click += (_, _) => LoadLuaWidget();
        buttons.Children.Add(load);

        var unload = SecondaryButton("Unload");
        unload.Width = 78;
        unload.Margin = new Thickness(8, 0, 0, 0);
        unload.Click += (_, _) => UnloadLuaWidget();
        buttons.Children.Add(unload);

        stack.Children.Add(footer);
        stack.Children.Add(_luaEditorPath);
        stack.Children.Add(_luaSettingsHost);
        return stack;
    }

    private TextBox LuaEditorControl()
    {
        return new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            Height = 150,
            Padding = new Thickness(10),
            FontFamily = CodeFont,
            FontSize = 13,
            Foreground = PrimaryTextBrush(),
            CaretBrush = PrimaryTextBrush(),
            Background = new SolidColorBrush(Color.FromArgb(74, 0, 0, 0)),
            BorderBrush = FieldBorderBrush(),
            BorderThickness = new Thickness(1),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap
        };
    }

    private void RefreshLuaWidgetList(string? selectScriptPath = null)
    {
        var previous = selectScriptPath ?? (_luaWidgetList.SelectedItem as LuaSettingsWidgetItem)?.ScriptPath;
        _luaWidgetList.Items.Clear();
        _luaEditor.Clear();
        _luaEditorPath.Text = string.Empty;
        ClearLuaSettingsPanel();

        try
        {
            var widgetsDirectory = CurrentLuaWidgetsDirectory();
            System.IO.Directory.CreateDirectory(widgetsDirectory);
            foreach (var scriptPath in System.IO.Directory.EnumerateFiles(widgetsDirectory, "*.lua", System.IO.SearchOption.AllDirectories)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                _luaWidgetList.Items.Add(new LuaSettingsWidgetItem(widgetsDirectory, scriptPath));
            }
        }
        catch (Exception ex)
        {
            SetLuaEditorStatus($"Folder error: {ex.Message}", false);
            return;
        }

        if (_luaWidgetList.Items.Count == 0)
        {
            SetLuaEditorStatus("No .lua files found.", false);
            return;
        }

        var selectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(previous))
        {
            for (var i = 0; i < _luaWidgetList.Items.Count; i++)
            {
                if (_luaWidgetList.Items[i] is LuaSettingsWidgetItem item
                    && string.Equals(item.ScriptPath, previous, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                    break;
                }
            }
        }

        _luaWidgetList.SelectedIndex = selectedIndex;
        SetLuaEditorStatus($"{_luaWidgetList.Items.Count} Lua file(s) found.", true);
    }

    private void OpenSelectedLuaScript()
    {
        if (SelectedLuaWidget() is not { } item)
        {
            return;
        }

        try
        {
            _luaEditor.Text = System.IO.File.ReadAllText(item.ScriptPath);
            _luaEditorPath.Text = item.ScriptPath;
            RefreshSelectedLuaSettings(item);
            SetLuaEditorStatus("Script loaded.", true);
        }
        catch (Exception ex)
        {
            _luaEditor.Clear();
            _luaEditorPath.Text = item.ScriptPath;
            ClearLuaSettingsPanel();
            SetLuaEditorStatus($"Load error: {ex.Message}", false);
        }
    }

    private void SaveLuaScript()
    {
        if (TrySaveLuaEditor(out _))
        {
            SetLuaEditorStatus("Saved. Lua syntax looks good.", true);
        }
    }

    private void LoadLuaWidget()
    {
        if (!TrySaveLuaEditor(out var item))
        {
            return;
        }

        try
        {
            WriteManifest(item.ManifestPath, BuildManifest(item, enabled: true));
            _luaEnabled.SelectedIndex = 0;
            _runtime.LoadLuaWidgets();
            RefreshLuaWidgetList(item.ScriptPath);
            SetLuaEditorStatus("Loaded widget.", true);
        }
        catch (Exception ex)
        {
            SetLuaEditorStatus($"Load error: {ex.Message}", false);
        }
    }

    private void UnloadLuaWidget()
    {
        if (SelectedLuaWidget() is not { } item)
        {
            SetLuaEditorStatus("Choose a widget first.", false);
            return;
        }

        try
        {
            WriteManifest(item.ManifestPath, BuildManifest(item, enabled: false));
            _runtime.ReloadLuaWidgets();
            RefreshLuaWidgetList(item.ScriptPath);
            SetLuaEditorStatus("Unloaded widget.", true);
        }
        catch (Exception ex)
        {
            SetLuaEditorStatus($"Unload error: {ex.Message}", false);
        }
    }

    private bool TrySaveLuaEditor(out LuaSettingsWidgetItem item)
    {
        item = null!;
        if (SelectedLuaWidget() is not { } selected)
        {
            SetLuaEditorStatus("Choose a widget first.", false);
            return false;
        }

        item = selected;
        try
        {
            ValidateLua(_luaEditor.Text, item.ScriptPath);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(item.ScriptPath)!);
            System.IO.File.WriteAllText(item.ScriptPath, _luaEditor.Text);
            _luaEditorPath.Text = item.ScriptPath;
            RefreshSelectedLuaSettings(item);
            return true;
        }
        catch (SyntaxErrorException ex)
        {
            SetLuaEditorStatus($"Lua syntax error: {ex.DecoratedMessage}", false);
        }
        catch (Exception ex)
        {
            SetLuaEditorStatus($"Save error: {ex.Message}", false);
        }

        return false;
    }

    private void RefreshSelectedLuaSettings(LuaSettingsWidgetItem item)
    {
        ClearLuaSettingsPanel();
        var definitions = ParseLuaSettingDefinitions(_luaEditor.Text);
        if (definitions.Count == 0)
        {
            return;
        }

        var widgetId = WidgetIdForSettings(item);
        if (!_value.LuaWidgetSettings.TryGetValue(widgetId, out var values))
        {
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _value.LuaWidgetSettings[widgetId] = values;
        }

        _luaSettingsHost.Children.Add(new TextBlock
        {
            Text = "Script settings",
            FontFamily = UiFont,
            FontSize = 12.5,
            Foreground = SoftTextBrush(),
            Margin = new Thickness(0, 0, 0, 8)
        });

        foreach (var definition in definitions)
        {
            if (!values.ContainsKey(definition.Key))
            {
                values[definition.Key] = definition.DefaultValue;
            }

            if (definition.Type == LuaSettingType.Text)
            {
                var textBox = TextBoxControl(values[definition.Key]);
                textBox.TextChanged += (_, _) => values[definition.Key] = textBox.Text;
                _luaSettingsHost.Children.Add(Field(definition.Label, textBox));
                _luaSettingBindings.Add(new LuaSettingBinding(widgetId, definition.Key, textBox));
            }
            else if (definition.Type == LuaSettingType.Slider)
            {
                var slider = new Slider
                {
                    Minimum = definition.Minimum,
                    Maximum = definition.Maximum,
                    Value = Math.Clamp(ParseSettingNumber(values[definition.Key], definition.DefaultNumber), definition.Minimum, definition.Maximum),
                    TickFrequency = definition.Step <= 0 ? 1 : definition.Step,
                    IsSnapToTickEnabled = definition.Step > 0,
                    Height = 32,
                    Foreground = PrimaryTextBrush(),
                    Background = FieldBrush(),
                    Template = RoundedSliderTemplate()
                };
                values[definition.Key] = slider.Value.ToString("0.###", CultureInfo.InvariantCulture);
                slider.ValueChanged += (_, _) => values[definition.Key] = slider.Value.ToString("0.###", CultureInfo.InvariantCulture);
                _luaSettingsHost.Children.Add(LuaSliderField(definition.Label, slider));
                _luaSettingBindings.Add(new LuaSettingBinding(widgetId, definition.Key, slider));
            }
            else if (definition.Type == LuaSettingType.Bool)
            {
                var combo = EnabledDropdown(IsLuaTruthy(values[definition.Key]));
                values[definition.Key] = combo.SelectedIndex == 0 ? "true" : "false";
                combo.SelectionChanged += (_, _) => values[definition.Key] = combo.SelectedIndex == 0 ? "true" : "false";
                _luaSettingsHost.Children.Add(Field(definition.Label, combo));
                _luaSettingBindings.Add(new LuaSettingBinding(widgetId, definition.Key, combo));
            }
            else if (definition.Type == LuaSettingType.Choice)
            {
                var options = definition.Options.Count > 0 ? definition.Options : [definition.DefaultValue];
                var combo = ComboBoxControl(options.ToArray());
                var selected = Math.Max(0, options.FindIndex(option => string.Equals(option, values[definition.Key], StringComparison.OrdinalIgnoreCase)));
                combo.SelectedIndex = selected;
                values[definition.Key] = options[selected];
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is string selectedValue)
                    {
                        values[definition.Key] = selectedValue;
                    }
                };
                _luaSettingsHost.Children.Add(Field(definition.Label, combo));
                _luaSettingBindings.Add(new LuaSettingBinding(widgetId, definition.Key, combo));
            }
            else if (definition.Type == LuaSettingType.Button)
            {
                var button = SecondaryButton(definition.Label);
                button.HorizontalAlignment = HorizontalAlignment.Left;
                button.MinWidth = 110;
                button.Click += (_, _) =>
                {
                    values[definition.Key] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                    SetLuaEditorStatus($"{definition.Label} clicked. Save to apply.", true);
                };
                _luaSettingsHost.Children.Add(Field("Action", button));
                _luaSettingBindings.Add(new LuaSettingBinding(widgetId, definition.Key, button));
            }
        }
    }

    private void ClearLuaSettingsPanel()
    {
        _luaSettingsHost.Children.Clear();
        _luaSettingBindings.Clear();
    }

    private static Grid LuaSliderField(string label, Slider slider)
    {
        var grid = FieldGrid(label);
        var value = new TextBlock
        {
            Width = 58,
            FontFamily = UiFont,
            FontSize = 12,
            Foreground = PrimaryTextBrush(),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        value.Text = slider.Value.ToString("0.###", CultureInfo.InvariantCulture);
        slider.ValueChanged += (_, _) => value.Text = slider.Value.ToString("0.###", CultureInfo.InvariantCulture);

        var sliderGrid = new Grid();
        sliderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sliderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sliderGrid.Children.Add(slider);
        Grid.SetColumn(value, 1);
        sliderGrid.Children.Add(value);

        Grid.SetColumn(sliderGrid, 1);
        grid.Children.Add(sliderGrid);
        return grid;
    }

    private static double ParseSettingNumber(string value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static List<LuaSettingDefinition> ParseLuaSettingDefinitions(string code)
    {
        var definitions = new List<LuaSettingDefinition>();
        foreach (var rawLine in code.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (TryReadLuaCallArgs(line, "widget.setting_text", out var textArgs) && textArgs.Count >= 2)
            {
                var key = CleanSettingKey(UnquoteLuaString(textArgs[0]));
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                definitions.Add(new LuaSettingDefinition(
                    LuaSettingType.Text,
                    key,
                    UnquoteLuaString(textArgs[1]),
                    textArgs.Count >= 3 ? UnquoteLuaString(textArgs[2]) : string.Empty,
                    0,
                    0,
                    0,
                    0,
                    []));
            }
            else if (TryReadLuaCallArgs(line, "widget.setting_slider", out var sliderArgs) && sliderArgs.Count >= 5)
            {
                var key = CleanSettingKey(UnquoteLuaString(sliderArgs[0]));
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var min = ParseSettingNumber(sliderArgs[2], 0);
                var max = ParseSettingNumber(sliderArgs[3], 100);
                if (max < min)
                {
                    (min, max) = (max, min);
                }

                var fallback = Math.Clamp(ParseSettingNumber(sliderArgs[4], min), min, max);
                var step = sliderArgs.Count >= 6 ? Math.Max(0, ParseSettingNumber(sliderArgs[5], 1)) : 1;
                definitions.Add(new LuaSettingDefinition(
                    LuaSettingType.Slider,
                    key,
                    UnquoteLuaString(sliderArgs[1]),
                    fallback.ToString("0.###", CultureInfo.InvariantCulture),
                    min,
                    max,
                    step,
                    fallback,
                    []));
            }
            else if (TryReadLuaCallArgs(line, "widget.setting_bool", out var boolArgs) && boolArgs.Count >= 2)
            {
                var key = CleanSettingKey(UnquoteLuaString(boolArgs[0]));
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var fallback = boolArgs.Count >= 3 && IsLuaTruthy(boolArgs[2]);
                definitions.Add(new LuaSettingDefinition(
                    LuaSettingType.Bool,
                    key,
                    UnquoteLuaString(boolArgs[1]),
                    fallback ? "true" : "false",
                    0,
                    0,
                    0,
                    0,
                    []));
            }
            else if (TryReadLuaCallArgs(line, "widget.setting_choice", out var choiceArgs) && choiceArgs.Count >= 3)
            {
                var key = CleanSettingKey(UnquoteLuaString(choiceArgs[0]));
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var options = ParseChoiceOptions(UnquoteLuaString(choiceArgs[2]));
                var fallback = choiceArgs.Count >= 4 ? UnquoteLuaString(choiceArgs[3]) : options.FirstOrDefault() ?? string.Empty;
                if (options.Count == 0)
                {
                    options.Add(fallback);
                }

                if (!options.Any(option => string.Equals(option, fallback, StringComparison.OrdinalIgnoreCase)))
                {
                    fallback = options[0];
                }

                definitions.Add(new LuaSettingDefinition(
                    LuaSettingType.Choice,
                    key,
                    UnquoteLuaString(choiceArgs[1]),
                    fallback,
                    0,
                    0,
                    0,
                    0,
                    options));
            }
            else if (TryReadLuaCallArgs(line, "widget.setting_button", out var buttonArgs) && buttonArgs.Count >= 2)
            {
                var key = CleanSettingKey(UnquoteLuaString(buttonArgs[0]));
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                definitions.Add(new LuaSettingDefinition(
                    LuaSettingType.Button,
                    key,
                    UnquoteLuaString(buttonArgs[1]),
                    string.Empty,
                    0,
                    0,
                    0,
                    0,
                    []));
            }
        }

        return definitions
            .GroupBy(definition => definition.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static bool TryReadLuaCallArgs(string line, string functionName, out List<string> args)
    {
        args = [];
        var start = line.IndexOf(functionName, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        var open = line.IndexOf('(', start + functionName.Length);
        if (open < 0)
        {
            return false;
        }

        var inString = false;
        var quote = '\0';
        var escaped = false;
        for (var i = open + 1; i < line.Length; i++)
        {
            var ch = line[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == quote)
                {
                    inString = false;
                }
            }
            else if (ch is '"' or '\'')
            {
                inString = true;
                quote = ch;
            }
            else if (ch == ')')
            {
                args = SplitLuaArgs(line[(open + 1)..i]);
                return true;
            }
        }

        return false;
    }

    private static List<string> SplitLuaArgs(string args)
    {
        var result = new List<string>();
        var start = 0;
        var inString = false;
        var quote = '\0';
        var escaped = false;
        for (var i = 0; i < args.Length; i++)
        {
            var ch = args[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == quote)
                {
                    inString = false;
                }
            }
            else if (ch is '"' or '\'')
            {
                inString = true;
                quote = ch;
            }
            else if (ch == ',')
            {
                result.Add(args[start..i].Trim());
                start = i + 1;
            }
        }

        result.Add(args[start..].Trim());
        return result;
    }

    private static string UnquoteLuaString(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return trimmed[1..^1]
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\'", "'", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return trimmed;
    }

    private static string CleanSettingKey(string key)
    {
        return new string(key.Trim().Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.').ToArray());
    }

    private static bool IsLuaTruthy(string value)
    {
        return value.Trim().Trim('"', '\'').ToLowerInvariant() is "true" or "yes" or "on" or "enabled" or "1";
    }

    private static List<string> ParseChoiceOptions(string value)
    {
        return value
            .Split(['|', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(option => !string.IsNullOrWhiteSpace(option))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string WidgetIdForSettings(LuaSettingsWidgetItem item)
    {
        try
        {
            if (System.IO.File.Exists(item.ManifestPath))
            {
                var manifest = LuaWidgetManifest.Load(item.ManifestPath);
                if (!string.IsNullOrWhiteSpace(manifest.Id))
                {
                    return manifest.Id.Trim();
                }
            }
        }
        catch
        {
            // Fall back to the id the editor would write for a new widget.
        }

        return ToWidgetId(System.IO.Path.GetFileNameWithoutExtension(item.ScriptPath));
    }

    private LuaSettingsWidgetItem? SelectedLuaWidget() => _luaWidgetList.SelectedItem as LuaSettingsWidgetItem;

    private string CurrentLuaWidgetsDirectory()
    {
        var configured = string.IsNullOrWhiteSpace(_luaWidgetsPath.Text)
            ? @"Widger\Lua"
            : _luaWidgetsPath.Text.Trim();
        return _runtime.ResolveLuaWidgetsPath(configured);
    }

    private static LuaWidgetManifest BuildManifest(LuaSettingsWidgetItem item, bool enabled)
    {
        var manifest = System.IO.File.Exists(item.ManifestPath)
            ? LuaWidgetManifest.Load(item.ManifestPath)
            : new LuaWidgetManifest
            {
                Width = 220,
                Height = 90,
                Left = 80,
                Top = 460,
                UpdateIntervalSeconds = 1
            };

        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            manifest.Id = string.Equals(System.IO.Path.GetFileName(item.ScriptPath), "main.lua", StringComparison.OrdinalIgnoreCase)
                ? ToWidgetId(System.IO.Path.GetFileName(item.Directory))
                : ToWidgetId(System.IO.Path.GetFileNameWithoutExtension(item.ScriptPath));
        }

        manifest.Name = string.IsNullOrWhiteSpace(manifest.Name) ? System.IO.Path.GetFileNameWithoutExtension(item.ScriptPath) : manifest.Name;
        manifest.Script = RelativePath(item.Directory, item.ScriptPath);
        manifest.Enabled = enabled;
        return manifest;
    }

    private static void ValidateLua(string code, string scriptPath)
    {
        var script = new Script(CoreModules.Preset_HardSandbox);
        script.LoadString(code, null, scriptPath);
    }

    private static string ResolveScriptPath(string widgetDirectory, string script)
    {
        var scriptName = string.IsNullOrWhiteSpace(script) ? "main.lua" : script.Trim();
        if (System.IO.Path.IsPathRooted(scriptName))
        {
            throw new InvalidOperationException("Manifest script path must be relative.");
        }

        var directory = System.IO.Path.GetFullPath(widgetDirectory);
        var directoryPrefix = directory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar;
        var scriptPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, scriptName));
        if (!scriptPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Manifest script path cannot leave the widget folder.");
        }

        return scriptPath;
    }

    private static string RelativePath(string fromDirectory, string path)
    {
        return System.IO.Path.GetRelativePath(fromDirectory, path);
    }

    private static string ToWidgetId(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var id = new string(chars).Trim('-');
        while (id.Contains("--", StringComparison.Ordinal))
        {
            id = id.Replace("--", "-", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(id) ? "lua-widget" : id;
    }

    private static void WriteManifest(string path, LuaWidgetManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        System.IO.File.WriteAllText(path, json);
    }

    private void SetLuaEditorStatus(string message, bool ok)
    {
        _luaEditorStatus.Text = message;
        _luaEditorStatus.Foreground = ok
            ? new SolidColorBrush(Color.FromRgb(135, 226, 162))
            : new SolidColorBrush(Color.FromRgb(255, 145, 145));
    }

    private static Border Section(string title, string subtitle, params UIElement[] fields)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = PanelBrush(),
            BorderBrush = new SolidColorBrush(Color.FromArgb(58, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var stack = new StackPanel();
        card.Child = stack;
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = UiFont,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = PrimaryTextBrush()
        });
        stack.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontFamily = UiFont,
            FontSize = 12,
            Foreground = MutedTextBrush(),
            Margin = new Thickness(0, 2, 0, 12)
        });

        foreach (var field in fields)
        {
            stack.Children.Add(field);
        }

        return card;
    }

    private static Grid Field(string label, Control control)
    {
        var grid = FieldGrid(label);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static Grid FolderField(string label, TextBox textBox, Button browse)
    {
        var grid = FieldGrid(label);
        var inputGrid = new Grid();
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(textBox, 0);
        inputGrid.Children.Add(textBox);
        browse.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(browse, 1);
        inputGrid.Children.Add(browse);

        Grid.SetColumn(inputGrid, 1);
        grid.Children.Add(inputGrid);
        return grid;
    }

    private static Grid SliderField(string label, Slider slider)
    {
        var grid = FieldGrid(label);
        var value = new TextBlock
        {
            Width = 48,
            FontFamily = UiFont,
            FontSize = 12,
            Foreground = PrimaryTextBrush(),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        value.Text = $"{(int)Math.Round(slider.Value)}%";
        slider.ValueChanged += (_, _) => value.Text = $"{(int)Math.Round(slider.Value)}%";

        var sliderGrid = new Grid();
        sliderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sliderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sliderGrid.Children.Add(slider);
        Grid.SetColumn(value, 1);
        sliderGrid.Children.Add(value);

        Grid.SetColumn(sliderGrid, 1);
        grid.Children.Add(sliderGrid);
        return grid;
    }

    private static Grid FieldGrid(string label)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(138) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = UiFont,
            FontSize = 12.5,
            Foreground = SoftTextBrush(),
            VerticalAlignment = VerticalAlignment.Center
        });

        return grid;
    }

    private static TextBox TextBoxControl(string value)
    {
        return new TextBox
        {
            Text = value,
            Height = 34,
            Padding = new Thickness(10, 0, 10, 1),
            FontFamily = UiFont,
            FontSize = 13,
            Foreground = PrimaryTextBrush(),
            CaretBrush = PrimaryTextBrush(),
            Background = FieldBrush(),
            BorderBrush = FieldBorderBrush(),
            BorderThickness = new Thickness(1),
            Template = RoundedTextBoxTemplate()
        };
    }

    private static ComboBox FormatDropdown(string value)
    {
        var combo = ComboBoxControl("12-hour", "24-hour");
        combo.SelectedIndex = WidgetConfig.Is24HourFormat(value) ? 1 : 0;
        return combo;
    }

    private static ComboBox UnitDropdown(string value)
    {
        var combo = ComboBoxControl("Fahrenheit", "Celsius");
        combo.SelectedIndex = string.Equals(value.Trim(), "c", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        return combo;
    }

    private static ComboBox EnabledDropdown(bool enabled)
    {
        var combo = ComboBoxControl("Enabled", "Disabled");
        combo.SelectedIndex = enabled ? 0 : 1;
        return combo;
    }

    private static ComboBox ComboBoxControl(params string[] items)
    {
        var combo = new ComboBox
        {
            Height = 34,
            Padding = new Thickness(8, 4, 8, 4),
            FontFamily = UiFont,
            FontSize = 13,
            Foreground = PrimaryTextBrush(),
            Background = FieldBrush(),
            BorderBrush = FieldBorderBrush(),
            BorderThickness = new Thickness(1),
            Template = RoundedComboBoxTemplate()
        };

        combo.Resources[SystemColors.WindowBrushKey] = FieldBrush();
        combo.Resources[SystemColors.ControlBrushKey] = FieldBrush();
        combo.Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(Color.FromArgb(170, 76, 132, 255));
        combo.Resources[SystemColors.HighlightTextBrushKey] = PrimaryTextBrush();

        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, FieldBrush()));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, PrimaryTextBrush()));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
        itemStyle.Setters.Add(new Setter(Control.TemplateProperty, RoundedComboBoxItemTemplate()));
        combo.ItemContainerStyle = itemStyle;

        foreach (var item in items)
        {
            combo.Items.Add(item);
        }

        return combo;
    }

    private static Slider SliderControl(int value, int minimum = 70, int maximum = 150, int tickFrequency = 5)
    {
        return new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = Math.Clamp(value, minimum, maximum),
            TickFrequency = tickFrequency,
            IsSnapToTickEnabled = true,
            Height = 32,
            Foreground = PrimaryTextBrush(),
            Background = FieldBrush(),
            Template = RoundedSliderTemplate()
        };
    }

    private static Button NavItem(string text, Action click)
    {
        var item = new Button
        {
            Content = text,
            Height = 34,
            Margin = new Thickness(0, 0, 0, 7),
            Background = NavInactiveBrush(),
            BorderBrush = NavInactiveBorderBrush(),
            BorderThickness = new Thickness(1),
            FontFamily = UiFont,
            FontSize = 12.5,
            FontWeight = FontWeights.Regular,
            Foreground = PrimaryTextBrush(),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 0, 12, 1),
            Template = RoundedButtonTemplate(7)
        };
        item.Click += (_, _) => click();
        return item;
    }

    private static void SetActiveNavItem(IEnumerable<Button> items, Button active)
    {
        foreach (var item in items)
        {
            var isActive = ReferenceEquals(item, active);
            item.Background = isActive ? NavActiveBrush() : NavInactiveBrush();
            item.BorderBrush = isActive ? NavActiveBorderBrush() : NavInactiveBorderBrush();
            item.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Regular;
        }
    }

    private static Button PrimaryButton(string text)
    {
        return Button(text, new SolidColorBrush(Color.FromRgb(86, 85, 255)), Brushes.White, 92);
    }

    private static Button SecondaryButton(string text)
    {
        return Button(text, new SolidColorBrush(Color.FromArgb(54, 255, 255, 255)), PrimaryTextBrush(), 92);
    }

    private static Button GhostButton(string text, double width)
    {
        return Button(text, new SolidColorBrush(Color.FromArgb(42, 255, 255, 255)), PrimaryTextBrush(), width);
    }

    private static Button Button(string text, Brush background, Brush foreground, double width)
    {
        return new Button
        {
            Content = text,
            Width = width,
            Height = 34,
            FontFamily = UiFont,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = foreground,
            Background = background,
            BorderBrush = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 0, 12, 1),
            Template = RoundedButtonTemplate(7)
        };
    }

    private static ControlTemplate RoundedTextBoxTemplate()
    {
        var template = new ControlTemplate(typeof(TextBox));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Control.BorderBrush)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(Control.BorderThickness)) { RelativeSource = RelativeSource.TemplatedParent });

        var host = new FrameworkElementFactory(typeof(ScrollViewer));
        host.Name = "PART_ContentHost";
        host.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        host.SetValue(FrameworkElement.HeightProperty, 22d);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetBinding(FrameworkElement.MarginProperty, new Binding(nameof(Control.Padding)) { RelativeSource = RelativeSource.TemplatedParent });
        border.AppendChild(host);

        template.VisualTree = border;
        return template;
    }

    private static ControlTemplate RoundedButtonTemplate(double radius)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Chrome";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Control.BorderBrush)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(Control.BorderThickness)) { RelativeSource = RelativeSource.TemplatedParent });

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetBinding(ContentPresenter.MarginProperty, new Binding(nameof(Control.Padding)) { RelativeSource = RelativeSource.TemplatedParent });
        content.SetBinding(ContentPresenter.HorizontalAlignmentProperty, new Binding(nameof(Control.HorizontalContentAlignment)) { RelativeSource = RelativeSource.TemplatedParent });
        content.SetBinding(ContentPresenter.VerticalAlignmentProperty, new Binding(nameof(Control.VerticalContentAlignment)) { RelativeSource = RelativeSource.TemplatedParent });
        border.AppendChild(content);

        template.VisualTree = border;
        return template;
    }

    private static ControlTemplate RoundedComboBoxTemplate()
    {
        const string xaml = """
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="{x:Type ComboBox}">
  <Grid>
    <ToggleButton x:Name="ToggleButton"
                  Background="{TemplateBinding Background}"
                  BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="{TemplateBinding BorderThickness}"
                  ClickMode="Press"
                  Focusable="False"
                  Foreground="{TemplateBinding Foreground}"
                  IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}"
                  Tag="{TemplateBinding SelectionBoxItem}">
      <ToggleButton.Template>
        <ControlTemplate TargetType="{x:Type ToggleButton}">
          <Border Background="{TemplateBinding Background}"
                  BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="{TemplateBinding BorderThickness}"
                  CornerRadius="7">
            <Grid>
              <ContentPresenter Content="{TemplateBinding Tag}"
                                Margin="10,0,30,1"
                                VerticalAlignment="Center"
                                HorizontalAlignment="Left"
                                TextElement.Foreground="{TemplateBinding Foreground}"
                                RecognizesAccessKey="True" />
              <Path Width="8"
                    Height="5"
                    Margin="0,0,12,0"
                    HorizontalAlignment="Right"
                    VerticalAlignment="Center"
                    Data="M 0 0 L 4 5 L 8 0 Z"
                    Fill="#FFFFFFFF" />
            </Grid>
          </Border>
        </ControlTemplate>
      </ToggleButton.Template>
    </ToggleButton>
    <Popup x:Name="PART_Popup"
           AllowsTransparency="True"
           Focusable="False"
           IsOpen="{TemplateBinding IsDropDownOpen}"
           Placement="Bottom"
           PopupAnimation="Fade">
      <Border MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"
              Background="#E0101010"
              BorderBrush="#55FFFFFF"
              BorderThickness="1"
              CornerRadius="7"
              Padding="3">
        <ScrollViewer MaxHeight="220"
                      CanContentScroll="True">
          <ItemsPresenter />
        </ScrollViewer>
      </Border>
    </Popup>
  </Grid>
</ControlTemplate>
""";
        return (ControlTemplate)XamlReader.Parse(xaml);
    }

    private static ControlTemplate RoundedComboBoxItemTemplate()
    {
        const string xaml = """
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="{x:Type ComboBoxItem}">
  <Border x:Name="Chrome"
          Background="{TemplateBinding Background}"
          CornerRadius="5"
          Padding="{TemplateBinding Padding}">
    <ContentPresenter TextElement.Foreground="{TemplateBinding Foreground}" />
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsHighlighted" Value="True">
      <Setter TargetName="Chrome" Property="Background" Value="#665655FF" />
    </Trigger>
    <Trigger Property="IsSelected" Value="True">
      <Setter TargetName="Chrome" Property="Background" Value="#805655FF" />
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""";
        return (ControlTemplate)XamlReader.Parse(xaml);
    }

    private static ControlTemplate RoundedSliderTemplate()
    {
        const string xaml = """
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="{x:Type Slider}">
  <Grid MinHeight="24">
    <Track x:Name="PART_Track"
           VerticalAlignment="Center">
      <Track.DecreaseRepeatButton>
        <RepeatButton Command="Slider.DecreaseLarge"
                      Height="6"
                      IsTabStop="False">
          <RepeatButton.Template>
            <ControlTemplate TargetType="{x:Type RepeatButton}">
              <Border Background="#CCFFFFFF"
                      CornerRadius="3" />
            </ControlTemplate>
          </RepeatButton.Template>
        </RepeatButton>
      </Track.DecreaseRepeatButton>
      <Track.IncreaseRepeatButton>
        <RepeatButton Command="Slider.IncreaseLarge"
                      Height="6"
                      IsTabStop="False">
          <RepeatButton.Template>
            <ControlTemplate TargetType="{x:Type RepeatButton}">
              <Border Background="#34000000"
                      CornerRadius="3" />
            </ControlTemplate>
          </RepeatButton.Template>
        </RepeatButton>
      </Track.IncreaseRepeatButton>
      <Track.Thumb>
        <Thumb Width="17"
               Height="17">
          <Thumb.Template>
            <ControlTemplate TargetType="{x:Type Thumb}">
              <Border Background="#FFFFFFFF"
                      BorderBrush="#66FFFFFF"
                      BorderThickness="1"
                      CornerRadius="8.5">
                <Border.Effect>
                  <DropShadowEffect BlurRadius="8"
                                    ShadowDepth="0"
                                    Opacity="0.35"
                                    Color="#000000" />
                </Border.Effect>
              </Border>
            </ControlTemplate>
          </Thumb.Template>
        </Thumb>
      </Track.Thumb>
    </Track>
  </Grid>
</ControlTemplate>
""";
        return (ControlTemplate)XamlReader.Parse(xaml);
    }

    private static Style RoundedScrollBarStyle()
    {
        const string xaml = """
<Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
       xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
       TargetType="{x:Type ScrollBar}">
  <Setter Property="Width" Value="10" />
  <Setter Property="MinWidth" Value="10" />
  <Setter Property="Background" Value="Transparent" />
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="{x:Type ScrollBar}">
        <Grid Background="Transparent"
              Width="10"
              Margin="2,4,0,4">
          <Track x:Name="PART_Track"
                 IsDirectionReversed="True">
            <Track.DecreaseRepeatButton>
              <RepeatButton Command="ScrollBar.PageUpCommand"
                            Opacity="0"
                            IsTabStop="False" />
            </Track.DecreaseRepeatButton>
            <Track.Thumb>
              <Thumb MinHeight="34">
                <Thumb.Template>
                  <ControlTemplate TargetType="{x:Type Thumb}">
                    <Border Background="#72FFFFFF"
                            CornerRadius="4"
                            Width="6"
                            HorizontalAlignment="Center" />
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command="ScrollBar.PageDownCommand"
                            Opacity="0"
                            IsTabStop="False" />
            </Track.IncreaseRepeatButton>
          </Track>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True">
            <Setter TargetName="PART_Track" Property="Opacity" Value="1" />
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
""";
        return (Style)XamlReader.Parse(xaml);
    }

    private static SolidColorBrush PanelBrush() => new(Color.FromArgb(20, 0, 0, 0));

    private static SolidColorBrush NavActiveBrush() => new(Color.FromArgb(62, 255, 255, 255));

    private static SolidColorBrush NavActiveBorderBrush() => new(Color.FromArgb(76, 255, 255, 255));

    private static SolidColorBrush NavInactiveBrush() => new(Color.FromArgb(12, 255, 255, 255));

    private static SolidColorBrush NavInactiveBorderBrush() => new(Color.FromArgb(28, 255, 255, 255));

    private static SolidColorBrush FieldBrush() => new(Color.FromArgb(38, 0, 0, 0));

    private static SolidColorBrush FieldBorderBrush() => new(Color.FromArgb(58, 255, 255, 255));

    private static SolidColorBrush PrimaryTextBrush() => new(Color.FromArgb(242, 255, 255, 255));

    private static SolidColorBrush SoftTextBrush() => new(Color.FromArgb(218, 235, 239, 255));

    private static SolidColorBrush MutedTextBrush() => new(Color.FromArgb(155, 218, 224, 255));

    private static void ApplyRoundedClip(Border border, double radius)
    {
        if (border.ActualWidth <= 0 || border.ActualHeight <= 0)
        {
            return;
        }

        border.Clip = new RectangleGeometry(
            new Rect(0, 0, border.ActualWidth, border.ActualHeight),
            radius,
            radius
        );
    }

    private sealed class LuaSettingsWidgetItem
    {
        internal LuaSettingsWidgetItem(string widgetsDirectory, string scriptPath)
        {
            WidgetsDirectory = System.IO.Path.GetFullPath(widgetsDirectory);
            ScriptPath = System.IO.Path.GetFullPath(scriptPath);
        }

        internal string WidgetsDirectory { get; }

        internal string ScriptPath { get; }

        internal string Directory => System.IO.Path.GetDirectoryName(ScriptPath) ?? WidgetsDirectory;

        internal string ManifestPath => System.IO.Path.Combine(Directory, "widget.json");

        public override string ToString()
        {
            var label = RelativePath(WidgetsDirectory, ScriptPath);
            try
            {
                var manifest = LuaWidgetManifest.Load(ManifestPath);
                var manifestScriptPath = ResolveScriptPath(Directory, manifest.Script);
                if (string.Equals(manifestScriptPath, ScriptPath, StringComparison.OrdinalIgnoreCase))
                {
                    return manifest.Enabled ? label : $"{label} (disabled)";
                }

                return label;
            }
            catch
            {
                return label;
            }
        }
    }

    private enum LuaSettingType
    {
        Text,
        Slider,
        Bool,
        Choice,
        Button
    }

    private sealed record LuaSettingDefinition(
        LuaSettingType Type,
        string Key,
        string Label,
        string DefaultValue,
        double Minimum,
        double Maximum,
        double Step,
        double DefaultNumber,
        List<string> Options);

    private sealed record LuaSettingBinding(string WidgetId, string Key, Control Control);
}

internal static class NativeWindowEffects
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    internal static void ApplyRoundedRegion(Window window, int radius)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
            {
                return;
            }

            var width = Math.Max(1, rect.Right - rect.Left);
            var height = Math.Max(1, rect.Bottom - rect.Top);
            var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius, radius);
            if (region != IntPtr.Zero)
            {
                SetWindowRgn(hwnd, region, true);
            }
        }
        catch
        {
            // Rounded WPF borders remain as the fallback if the native region call is unavailable.
        }
    }
}
