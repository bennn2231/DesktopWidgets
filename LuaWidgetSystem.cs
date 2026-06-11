using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MoonSharp.Interpreter;

namespace WidgesDesktopDotNet;

internal sealed class LuaWidgetManager : IDisposable
{
    private readonly WidgetRuntime _runtime;
    private readonly string _skinRoot;
    private readonly List<LuaWidgetInstance> _widgets = [];

    internal LuaWidgetManager(WidgetRuntime runtime, string skinRoot)
    {
        _runtime = runtime;
        _skinRoot = skinRoot;
        UserData.RegisterType<LuaTextElement>();
        UserData.RegisterType<LuaRectangleElement>();
        UserData.RegisterType<LuaCircleElement>();
        UserData.RegisterType<LuaLineElement>();
        UserData.RegisterType<LuaPolygonElement>();
        UserData.RegisterType<LuaArcElement>();
        UserData.RegisterType<LuaImageElement>();
        UserData.RegisterType<LuaTimerHandle>();
    }

    internal void LoadWidgets(WidgetConfig config)
    {
        if (!config.LuaWidgetsEnabled)
        {
            return;
        }

        var widgetsPath = ResolveWidgetsPath(config.LuaWidgetsPath);
        System.IO.Directory.CreateDirectory(widgetsPath);

        var rootManifestPath = System.IO.Path.Combine(widgetsPath, "widget.json");
        if (System.IO.File.Exists(rootManifestPath))
        {
            LoadWidgetDirectory(widgetsPath, System.IO.Path.GetFileName(widgetsPath), config);
        }

        foreach (var directory in System.IO.Directory.EnumerateDirectories(widgetsPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = System.IO.Path.Combine(directory, "widget.json");
            if (!System.IO.File.Exists(manifestPath))
            {
                continue;
            }

            LoadWidgetDirectory(directory, System.IO.Path.GetFileName(directory), config);
        }
    }

    private void LoadWidgetDirectory(string directory, string fallbackId, WidgetConfig config)
    {
        try
        {
            var manifest = LuaWidgetManifest.Load(System.IO.Path.Combine(directory, "widget.json"));
            if (!manifest.Enabled)
            {
                return;
            }

            var id = string.IsNullOrWhiteSpace(manifest.Id)
                ? fallbackId
                : manifest.Id.Trim();

            var state = config.LuaWidgets.TryGetValue(id, out var savedState) ? savedState : null;
            var settings = config.LuaWidgetSettings.TryGetValue(id, out var savedSettings) ? savedSettings : null;
            var instance = new LuaWidgetInstance(_runtime, directory, id, manifest, state, settings);
            instance.Start();
            _widgets.Add(instance);
        }
        catch (Exception ex)
        {
            var fallback = LuaWidgetManifest.ErrorFallback(fallbackId);
            var instance = new LuaWidgetInstance(_runtime, directory, fallbackId, fallback, null, null);
            instance.StartWithError($"Manifest error: {ex.Message}");
            _widgets.Add(instance);
        }
    }

    internal void Reload(WidgetConfig config)
    {
        DisposeWidgets();
        LoadWidgets(config);
    }

    internal void SavePositions(WidgetConfig config)
    {
        foreach (var widget in _widgets)
        {
            config.LuaWidgets[widget.Id] = widget.GetState();
        }
    }

    internal void DropTopMost()
    {
        foreach (var widget in _widgets)
        {
            widget.DropTopMost();
        }
    }

    internal void EmbedAll()
    {
        foreach (var widget in _widgets)
        {
            widget.Embed();
        }
    }

    public void Dispose() => DisposeWidgets();

    private void DisposeWidgets()
    {
        foreach (var widget in _widgets)
        {
            widget.Dispose();
        }

        _widgets.Clear();
    }

    private string ResolveWidgetsPath(string path)
    {
        var trimmed = string.IsNullOrWhiteSpace(path) ? @"Widger\Lua" : path.Trim();
        if (System.IO.Path.IsPathRooted(trimmed))
        {
            return trimmed;
        }

        return System.IO.Path.GetFullPath(System.IO.Path.Combine(_skinRoot, trimmed));
    }
}

internal static class LuaHttpPolicy
{
    internal static readonly HashSet<string> ApprovedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.open-meteo.com",
        "geocoding-api.open-meteo.com",
        "api.github.com",
        "raw.githubusercontent.com",
        "api.spotify.com",
        "accounts.spotify.com",
        "i.scdn.co",
        "api.robinhood.com",
        "nummus.robinhood.com",
        "trading.robinhood.com",
        "status.robinhood.com",
        "rpc.testnet.chain.robinhood.com",
        "sequencer.testnet.chain.robinhood.com",
        "explorer.testnet.chain.robinhood.com"
    };
}

internal sealed class LuaWidgetInstance : IDisposable
{
    private const int MaxHttpBytes = 1024 * 1024;
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private sealed class LuaHttpCacheEntry
    {
        internal string Text { get; set; } = string.Empty;
        internal DateTimeOffset FetchedAtUtc { get; set; }
        internal bool InFlight { get; set; }
    }

    private readonly WidgetRuntime _runtime;
    private readonly string _widgetDirectory;
    private readonly LuaWidgetManifest _manifest;
    private readonly LuaWidgetWindow _window;
    private readonly DispatcherTimer _timer;
    private readonly List<DispatcherTimer> _scriptTimers = [];
    private readonly Dictionary<string, string> _settings;
    private readonly object _httpCacheLock = new();
    private readonly Dictionary<string, LuaHttpCacheEntry> _httpCache = new(StringComparer.Ordinal);
    private Script? _script;

    internal string Id { get; }

    internal LuaWidgetInstance(
        WidgetRuntime runtime,
        string widgetDirectory,
        string id,
        LuaWidgetManifest manifest,
        LuaWidgetState? state,
        IReadOnlyDictionary<string, string>? settings)
    {
        _runtime = runtime;
        _widgetDirectory = widgetDirectory;
        _manifest = manifest;
        Id = id;
        _settings = settings?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var width = state?.Width > 0 ? state.Width : manifest.Width;
        var height = state?.Height > 0 ? state.Height : manifest.Height;
        var left = state?.Left ?? manifest.Left;
        var top = state?.Top ?? manifest.Top;

        _window = new LuaWidgetWindow(_runtime, manifest.NameOrId(id), left, top, width, height);
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(manifest.UpdateIntervalSeconds, 0.05, 86400))
        };
        _timer.Tick += (_, _) => CallFunction("on_update");
        _window.LeftClicked += HandleLeftClick;
    }

    internal void Start()
    {
        _window.Show();
        LoadScript();
        CallFunction("on_load");
        CallFunction("on_update");
        _timer.Start();
    }

    internal void StartWithError(string message)
    {
        _window.Show();
        ShowError(message);
    }

    internal void DropTopMost() => _window.Topmost = false;

    internal void Embed()
    {
        _window.Topmost = false;
        DesktopShell.Embed(_window);
    }

    internal LuaWidgetState GetState()
    {
        var (left, top) = DesktopShell.GetScreenPosition(_window);
        return new()
        {
            Left = left,
            Top = top,
            Width = (int)Math.Round(_window.Width),
            Height = (int)Math.Round(_window.Height)
        };
    }

    public void Dispose()
    {
        try
        {
            CallFunction("on_close");
        }
        catch
        {
            // Best-effort shutdown hook only.
        }

        _timer.Stop();
        StopScriptTimers();
        _window.CloseWithoutSaving();
    }

    private void LoadScript()
    {
        var scriptPath = System.IO.Path.Combine(_widgetDirectory, _manifest.Script);
        if (!System.IO.File.Exists(scriptPath))
        {
            ShowError($"Script not found: {_manifest.Script}");
            return;
        }

        _script = new Script(CoreModules.Preset_HardSandbox);
        _script.Globals["widget"] = CreateWidgetApi(_script);
        _script.Globals["http"] = CreateHttpApi(_script);
        _script.Globals["json"] = CreateJsonApi(_script);
        _script.Globals["spotify"] = CreateSpotifyApi(_script);

        try
        {
            var code = System.IO.File.ReadAllText(scriptPath);
            _script.DoString(code, null, scriptPath);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private bool HandleLeftClick(double x, double y)
    {
        if (_window.HandleGuiClick(x, y))
        {
            CallFunction("on_update");
            return true;
        }

        return CallFunction("on_click", DynValue.NewNumber(x), DynValue.NewNumber(y));
    }

    private bool CallFunction(string name, params DynValue[] args)
    {
        if (_script == null)
        {
            return false;
        }

        var fn = _script.Globals.Get(name);
        if (fn.Type != DataType.Function)
        {
            return false;
        }

        try
        {
            _script.Call(fn, args);
            return true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return true;
        }
    }

    private Table CreateWidgetApi(Script script)
    {
        var api = new Table(script);
        api["text"] = DynValue.NewCallback((_, args) =>
        {
            var element = _window.AddText(
                ArgString(args, 0, string.Empty),
                ArgDouble(args, 1, 0),
                ArgDouble(args, 2, 0),
                ArgDouble(args, 3, 18),
                ArgString(args, 4, "#FFFFFF"),
                ArgString(args, 5, "normal"),
                ArgString(args, 6, "Segoe UI"));
            return UserData.Create(element);
        });
        api["rect"] = DynValue.NewCallback((_, args) =>
        {
            var element = _window.AddRectangle(
                ArgDouble(args, 0, 0),
                ArgDouble(args, 1, 0),
                ArgDouble(args, 2, 100),
                ArgDouble(args, 3, 40),
                ArgString(args, 4, "#FFFFFF"),
                ArgDouble(args, 5, 1),
                ArgDouble(args, 6, 0),
                ArgString(args, 7, "transparent"),
                ArgDouble(args, 8, 0));
            return UserData.Create(element);
        });
        api["circle"] = DynValue.NewCallback((_, args) =>
        {
            var element = _window.AddCircle(
                ArgDouble(args, 0, 0),
                ArgDouble(args, 1, 0),
                ArgDouble(args, 2, 40),
                ArgString(args, 3, "#FFFFFF"),
                ArgDouble(args, 4, 1),
                ArgString(args, 5, "transparent"),
                ArgDouble(args, 6, 0));
            return UserData.Create(element);
        });
        api["line"] = DynValue.NewCallback((_, args) =>
        {
            var element = _window.AddLine(
                ArgDouble(args, 0, 0),
                ArgDouble(args, 1, 0),
                ArgDouble(args, 2, 100),
                ArgDouble(args, 3, 0),
                ArgString(args, 4, "#FFFFFF"),
                ArgDouble(args, 5, 1),
                ArgDouble(args, 6, 1));
            return UserData.Create(element);
        });
        api["polygon"] = DynValue.NewCallback((_, args) =>
        {
            var element = _window.AddPolygon(
                PointsFromDynValue(ArgValue(args, 0)),
                ArgString(args, 1, "#FFFFFF"),
                ArgDouble(args, 2, 1),
                ArgString(args, 3, "transparent"),
                ArgDouble(args, 4, 0));
            return UserData.Create(element);
        });
        api["arc"] = DynValue.NewCallback((_, args) =>
        {
            var element = _window.AddArc(
                ArgDouble(args, 0, 0),
                ArgDouble(args, 1, 0),
                ArgDouble(args, 2, 100),
                ArgDouble(args, 3, 100),
                ArgDouble(args, 4, 0),
                ArgDouble(args, 5, 90),
                ArgString(args, 6, "#FFFFFF"),
                ArgDouble(args, 7, 2),
                ArgDouble(args, 8, 1));
            return UserData.Create(element);
        });
        api["image"] = DynValue.NewCallback((_, args) =>
        {
            var element = _window.AddImage(
                ResolveAssetPath(ArgString(args, 0, string.Empty)),
                ArgDouble(args, 1, 0),
                ArgDouble(args, 2, 0),
                ArgDouble(args, 3, 64),
                ArgDouble(args, 4, 64),
                ArgDouble(args, 5, 1));
            return UserData.Create(element);
        });
        api["clear"] = DynValue.NewCallback((_, _) =>
        {
            _window.Clear();
            return DynValue.Nil;
        });
        api["set_size"] = DynValue.NewCallback((_, args) =>
        {
            _window.SetWidgetSize(ArgDouble(args, 0, _window.Width), ArgDouble(args, 1, _window.Height));
            return DynValue.Nil;
        });
        api["set_background"] = DynValue.NewCallback((_, args) =>
        {
            _window.SetBackground(ArgString(args, 0, "transparent"), ArgDouble(args, 1, 1));
            return DynValue.Nil;
        });
        api["now"] = DynValue.NewCallback((_, args) =>
        {
            var format = ArgString(args, 0, "HH:mm:ss");
            return DynValue.NewString(DateTime.Now.ToString(format, CultureInfo.InvariantCulture));
        });
        api["log"] = DynValue.NewCallback((_, args) =>
        {
            AppendLog(ArgString(args, 0, string.Empty));
            return DynValue.Nil;
        });
        api["after"] = DynValue.NewCallback((_, args) =>
        {
            return UserData.Create(CreateScriptTimer(script, ArgDouble(args, 0, 0), ArgValue(args, 1), repeat: false));
        });
        api["every"] = DynValue.NewCallback((_, args) =>
        {
            return UserData.Create(CreateScriptTimer(script, ArgDouble(args, 0, 0), ArgValue(args, 1), repeat: true));
        });
        api["setting_text"] = DynValue.NewCallback((_, args) =>
        {
            var key = ArgString(args, 0, string.Empty);
            var fallback = ArgString(args, 2, string.Empty);
            return DynValue.NewString(SettingText(key, fallback));
        });
        api["setting_slider"] = DynValue.NewCallback((_, args) =>
        {
            var key = ArgString(args, 0, string.Empty);
            var fallback = ArgDouble(args, 4, ArgDouble(args, 3, 0));
            return DynValue.NewNumber(SettingNumber(key, fallback));
        });
        api["setting_bool"] = DynValue.NewCallback((_, args) =>
        {
            var key = ArgString(args, 0, string.Empty);
            var fallback = ArgBool(args, 2, false);
            return DynValue.NewBoolean(SettingBool(key, fallback));
        });
        api["setting_choice"] = DynValue.NewCallback((_, args) =>
        {
            var key = ArgString(args, 0, string.Empty);
            var fallback = ArgString(args, 3, string.Empty);
            return DynValue.NewString(SettingText(key, fallback));
        });
        api["setting_button"] = DynValue.NewCallback((_, args) =>
        {
            var key = ArgString(args, 0, string.Empty);
            return DynValue.NewString(SettingText(key, string.Empty));
        });
        api["setting"] = DynValue.NewCallback((_, args) =>
        {
            var key = ArgString(args, 0, string.Empty);
            var fallback = ArgString(args, 1, string.Empty);
            return DynValue.NewString(SettingText(key, fallback));
        });
        api["ui"] = CreateUiApi(script);
        return api;
    }

    private Table CreateUiApi(Script script)
    {
        var api = new Table(script);
        api["begin"] = DynValue.NewCallback((_, _) =>
        {
            _window.BeginGuiFrame();
            return DynValue.Nil;
        });
        api["end_frame"] = DynValue.NewCallback((_, _) => DynValue.Nil);
        api["panel"] = DynValue.NewCallback((_, args) =>
        {
            _window.GuiPanel(
                ArgDouble(args, 0, 0),
                ArgDouble(args, 1, 0),
                ArgDouble(args, 2, 100),
                ArgDouble(args, 3, 40),
                ArgString(args, 4, "#111111"),
                ArgDouble(args, 5, 0.85),
                ArgDouble(args, 6, 10),
                ArgString(args, 7, "transparent"),
                ArgDouble(args, 8, 0));
            return DynValue.Nil;
        });
        api["label"] = DynValue.NewCallback((_, args) =>
        {
            _window.GuiLabel(
                ArgString(args, 0, string.Empty),
                ArgDouble(args, 1, 0),
                ArgDouble(args, 2, 0),
                ArgDouble(args, 3, 14),
                ArgString(args, 4, "#ffffff"),
                ArgString(args, 5, "normal"),
                ArgString(args, 6, "Segoe UI"));
            return DynValue.Nil;
        });
        api["button"] = DynValue.NewCallback((_, args) =>
        {
            var text = ArgString(args, 0, "Button");
            var x = ArgDouble(args, 1, 0);
            var y = ArgDouble(args, 2, 0);
            var width = ArgDouble(args, 3, 80);
            var height = ArgDouble(args, 4, 28);
            var key = ArgString(args, 5, $"button:{text}:{x:0}:{y:0}");
            return DynValue.NewBoolean(_window.GuiButton(text, x, y, width, height, key));
        });
        api["icon_button"] = DynValue.NewCallback((_, args) =>
        {
            var icon = ArgString(args, 0, "play");
            var x = ArgDouble(args, 1, 0);
            var y = ArgDouble(args, 2, 0);
            var width = ArgDouble(args, 3, 32);
            var height = ArgDouble(args, 4, 28);
            var key = ArgString(args, 5, $"icon:{icon}:{x:0}:{y:0}");
            return DynValue.NewBoolean(_window.GuiIconButton(icon, x, y, width, height, key));
        });
        return api;
    }

    private string SettingText(string key, string fallback)
    {
        return !string.IsNullOrWhiteSpace(key) && _settings.TryGetValue(key.Trim(), out var value)
            ? value
            : fallback;
    }

    private double SettingNumber(string key, double fallback)
    {
        var value = SettingText(key, string.Empty);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private bool SettingBool(string key, bool fallback)
    {
        var value = SettingText(key, string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "true" or "yes" or "on" or "enabled" or "1" => true,
            "false" or "no" or "off" or "disabled" or "0" => false,
            _ => fallback
        };
    }

    private Table CreateHttpApi(Script script)
    {
        var api = new Table(script);
        api["get"] = DynValue.NewCallback((_, args) =>
        {
            var text = HttpGet(ArgString(args, 0, string.Empty), args.Count > 1 ? args[1] : DynValue.Nil);
            return DynValue.NewString(text);
        });
        api["get_async"] = DynValue.NewCallback((_, args) =>
        {
            var url = ArgString(args, 0, string.Empty);
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new ScriptRuntimeException("http.get_async(url, [headers], callback) requires a url.");
            }

            DynValue headers;
            DynValue callback;
            if (args.Count >= 2 && args[1].Type == DataType.Function)
            {
                headers = DynValue.Nil;
                callback = args[1];
            }
            else
            {
                headers = args.Count > 1 ? args[1] : DynValue.Nil;
                callback = args.Count > 2 ? args[2] : DynValue.Nil;
            }

            if (callback.Type != DataType.Function)
            {
                throw new ScriptRuntimeException("http.get_async(url, [headers], callback) requires a callback function.");
            }

            System.Threading.Tasks.Task.Run(() =>
            {
                string result;
                string? error = null;
                try
                {
                    result = HttpGet(url, headers);
                }
                catch (Exception ex)
                {
                    result = string.Empty;
                    error = ex.Message;
                }

                _window.Dispatcher.BeginInvoke(() =>
                {
                    if (_script == null)
                    {
                        return;
                    }

                    try
                    {
                        var ok = error == null;
                        _script.Call(callback, DynValue.NewBoolean(ok), DynValue.NewString(ok ? result : error ?? string.Empty));
                    }
                    catch (Exception ex)
                    {
                        ShowError(ex.Message);
                    }
                });
            });

            return DynValue.True;
        });
        api["get_json_async"] = DynValue.NewCallback((_, args) =>
        {
            var url = ArgString(args, 0, string.Empty);
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new ScriptRuntimeException("http.get_json_async(url, [headers], callback) requires a url.");
            }

            DynValue headers;
            DynValue callback;
            if (args.Count >= 2 && args[1].Type == DataType.Function)
            {
                headers = DynValue.Nil;
                callback = args[1];
            }
            else
            {
                headers = args.Count > 1 ? args[1] : DynValue.Nil;
                callback = args.Count > 2 ? args[2] : DynValue.Nil;
            }

            if (callback.Type != DataType.Function)
            {
                throw new ScriptRuntimeException("http.get_json_async(url, [headers], callback) requires a callback function.");
            }

            System.Threading.Tasks.Task.Run(() =>
            {
                string result;
                string? error = null;
                try
                {
                    result = HttpGet(url, headers);
                }
                catch (Exception ex)
                {
                    result = string.Empty;
                    error = ex.Message;
                }

                _window.Dispatcher.BeginInvoke(() =>
                {
                    if (_script == null)
                    {
                        return;
                    }

                    try
                    {
                        if (error != null)
                        {
                            _script.Call(callback, DynValue.False, DynValue.NewString(error));
                            return;
                        }

                        using var doc = JsonDocument.Parse(result);
                        var parsed = JsonToDynValue(script, doc.RootElement);
                        _script.Call(callback, DynValue.True, parsed);
                    }
                    catch (Exception ex)
                    {
                        // Parsing / callback errors: surface as Lua error UI for easier debugging.
                        ShowError(ex.Message);
                    }
                });
            });

            return DynValue.True;
        });
        api["get_cached"] = DynValue.NewCallback((_, args) =>
        {
            var url = ArgString(args, 0, string.Empty);
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new ScriptRuntimeException("http.get_cached(url, [headers], [ttlSeconds], [fallback]) requires a url.");
            }

            var headers = args.Count > 1 ? args[1] : DynValue.Nil;
            var ttlSeconds = args.Count > 2 ? ArgDouble(args, 2, 60) : 60;
            var fallback = args.Count > 3 ? ArgString(args, 3, string.Empty) : string.Empty;

            var cacheKey = HttpCacheKey(url, headers);
            var nowUtc = DateTimeOffset.UtcNow;
            var ttl = TimeSpan.FromSeconds(Math.Clamp(ttlSeconds, 0.1, 86_400));

            LuaHttpCacheEntry? entry;
            lock (_httpCacheLock)
            {
                _httpCache.TryGetValue(cacheKey, out entry);
                if (entry != null)
                {
                    var fresh = (nowUtc - entry.FetchedAtUtc) <= ttl;
                    if (fresh)
                    {
                        return DynValue.NewString(entry.Text);
                    }

                    if (!entry.InFlight)
                    {
                        entry.InFlight = true;
                    }
                    else
                    {
                        return DynValue.NewString(string.IsNullOrEmpty(entry.Text) ? fallback : entry.Text);
                    }
                }
                else
                {
                    entry = new LuaHttpCacheEntry { InFlight = true };
                    _httpCache[cacheKey] = entry;
                }
            }

            System.Threading.Tasks.Task.Run(() =>
            {
                string text;
                try
                {
                    text = HttpGet(url, headers);
                }
                catch
                {
                    text = string.Empty;
                }

                _window.Dispatcher.BeginInvoke(() =>
                {
                    lock (_httpCacheLock)
                    {
                        if (_httpCache.TryGetValue(cacheKey, out var existing))
                        {
                            existing.InFlight = false;
                            if (!string.IsNullOrEmpty(text))
                            {
                                existing.Text = text;
                                existing.FetchedAtUtc = DateTimeOffset.UtcNow;
                            }
                        }
                    }
                });
            });

            return DynValue.NewString(string.IsNullOrEmpty(entry.Text) ? fallback : entry.Text);
        });
        api["get_json"] = DynValue.NewCallback((_, args) =>
        {
            var text = HttpSend("GET", ArgString(args, 0, string.Empty), args.Count > 1 ? args[1] : DynValue.Nil, DynValue.Nil);
            using var doc = JsonDocument.Parse(text);
            return JsonToDynValue(script, doc.RootElement);
        });
        api["put"] = DynValue.NewCallback((_, args) =>
        {
            return DynValue.NewString(HttpSend("PUT", ArgString(args, 0, string.Empty), ArgValue(args, 1), ArgValue(args, 2)));
        });
        api["post"] = DynValue.NewCallback((_, args) =>
        {
            return DynValue.NewString(HttpSend("POST", ArgString(args, 0, string.Empty), ArgValue(args, 1), ArgValue(args, 2)));
        });
        api["approved_hosts"] = DynValue.NewCallback((_, _) =>
        {
            var hosts = new Table(script);
            var index = 1;
            foreach (var host in LuaHttpPolicy.ApprovedHosts.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                hosts[index++] = host;
            }

            return DynValue.NewTable(hosts);
        });
        return api;
    }

    private static string HttpCacheKey(string url, DynValue headers)
    {
        var builder = new StringBuilder();
        builder.Append(url.Trim());
        if (headers.Type != DataType.Table)
        {
            return builder.ToString();
        }

        var headerPairs = new List<(string Name, string Value)>();
        foreach (var pair in headers.Table.Pairs)
        {
            var name = DynValueToString(pair.Key).Trim();
            var value = DynValueToString(pair.Value).Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                headerPairs.Add((name, value));
            }
        }

        headerPairs.Sort((a, b) =>
        {
            var byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.Compare(a.Value, b.Value, StringComparison.Ordinal);
        });

        foreach (var (name, value) in headerPairs)
        {
            builder.Append("\n");
            builder.Append(name);
            builder.Append(":");
            builder.Append(value);
        }

        return builder.ToString();
    }

    private Table CreateJsonApi(Script script)
    {
        var api = new Table(script);
        api["parse"] = DynValue.NewCallback((_, args) =>
        {
            using var doc = JsonDocument.Parse(ArgString(args, 0, string.Empty));
            return JsonToDynValue(script, doc.RootElement);
        });
        return api;
    }

    private Table CreateSpotifyApi(Script script)
    {
        var api = new Table(script);
        api["configure"] = DynValue.NewCallback((_, args) =>
        {
            return DynValue.NewString(_runtime.Spotify.Configure(
                ArgString(args, 0, string.Empty),
                args.Count > 1 && !args[1].IsNil() ? ArgString(args, 1, string.Empty) : null));
        });
        api["login"] = DynValue.NewCallback((_, _) =>
        {
            return DynValue.NewString(_runtime.Spotify.Login());
        });
        api["logout"] = DynValue.NewCallback((_, _) =>
        {
            return DynValue.NewString(_runtime.Spotify.Logout());
        });
        api["status"] = DynValue.NewCallback((_, _) =>
        {
            return DynValue.NewString(_runtime.Spotify.LastStatus);
        });
        api["error"] = DynValue.NewCallback((_, _) =>
        {
            return DynValue.NewString(_runtime.Spotify.LastError);
        });
        api["current"] = DynValue.NewCallback((_, _) =>
        {
            return DynValue.NewTable(SpotifyTrackToTable(script, _runtime.Spotify.Current()));
        });
        api["play"] = DynValue.NewCallback((_, _) =>
        {
            return DynValue.NewBoolean(_runtime.Spotify.Play());
        });
        api["pause"] = DynValue.NewCallback((_, _) =>
        {
            return DynValue.NewBoolean(_runtime.Spotify.Pause());
        });
        api["next"] = DynValue.NewCallback((_, _) =>
        {
            return DynValue.NewBoolean(_runtime.Spotify.Next());
        });
        api["previous"] = DynValue.NewCallback((_, _) =>
        {
            return DynValue.NewBoolean(_runtime.Spotify.Previous());
        });
        return api;
    }

    private static Table SpotifyTrackToTable(Script script, SpotifyCurrentTrack track)
    {
        var table = new Table(script)
        {
            ["ok"] = track.Ok,
            ["connected"] = track.Connected,
            ["is_playing"] = track.IsPlaying,
            ["name"] = track.Name,
            ["artist"] = track.Artist,
            ["album_image_url"] = track.AlbumImageUrl,
            ["status"] = track.Status,
            ["error"] = track.Error
        };
        return table;
    }

    private LuaTimerHandle CreateScriptTimer(Script script, double milliseconds, DynValue callback, bool repeat)
    {
        if (callback.Type != DataType.Function)
        {
            throw new ScriptRuntimeException("Timer callback must be a function.");
        }

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 1, 86_400_000))
        };
        var handle = new LuaTimerHandle(timer, () => _scriptTimers.Remove(timer));
        timer.Tick += (_, _) =>
        {
            if (!repeat)
            {
                handle.cancel();
            }

            try
            {
                script.Call(callback);
            }
            catch (Exception ex)
            {
                handle.cancel();
                ShowError(ex.Message);
            }
        };

        _scriptTimers.Add(timer);
        timer.Start();
        return handle;
    }

    private void StopScriptTimers()
    {
        foreach (var timer in _scriptTimers.ToArray())
        {
            timer.Stop();
        }

        _scriptTimers.Clear();
    }

    private string ResolveAssetPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            return value;
        }

        if (System.IO.Path.IsPathRooted(value))
        {
            return value;
        }

        return System.IO.Path.GetFullPath(System.IO.Path.Combine(_widgetDirectory, value));
    }

    private void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
        System.IO.File.AppendAllText(System.IO.Path.Combine(_widgetDirectory, "widget.log"), line);
    }

    private void ShowError(string message)
    {
        _window.Clear();
        _window.SetBackground("#2b1111", 0.82);
        _window.AddText("Lua widget error", 10, 10, 14, "#ff8080", "bold", "Segoe UI");
        _window.AddText(message, 10, 34, 12, "#ffffff", "normal", "Segoe UI").set_width(Math.Max(120, _window.Width - 20));
        AppendLog($"ERROR: {message}");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = MaxHttpBytes
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WidgesLua/1.0");
        return client;
    }

    private static string HttpGet(string url, DynValue headers)
    {
        return HttpSend("GET", url, headers, DynValue.Nil);
    }

    private static string HttpSend(string method, string url, DynValue headers, DynValue body)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ScriptRuntimeException("Lua HTTP only allows absolute https URLs.");
        }

        if (!LuaHttpPolicy.ApprovedHosts.Contains(uri.Host))
        {
            throw new ScriptRuntimeException($"Host is not approved for Lua HTTP: {uri.Host}");
        }

        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        if (headers.Type == DataType.Table)
        {
            foreach (var pair in headers.Table.Pairs)
            {
                var name = DynValueToString(pair.Key);
                var value = DynValueToString(pair.Value);
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        if (!body.IsNil())
        {
            request.Content = new StringContent(
                body.Type == DataType.String ? body.String : body.ToPrintString(),
                Encoding.UTF8,
                "application/json");
        }

        using var response = SharedHttpClient.Send(request);
        response.EnsureSuccessStatusCode();
        var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        if (bytes.Length > MaxHttpBytes)
        {
            throw new ScriptRuntimeException("HTTP response is too large.");
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static DynValue JsonToDynValue(Script script, JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => JsonObjectToTable(script, element),
            JsonValueKind.Array => JsonArrayToTable(script, element),
            JsonValueKind.String => DynValue.NewString(element.GetString() ?? string.Empty),
            JsonValueKind.Number => DynValue.NewNumber(element.TryGetDouble(out var n) ? n : 0),
            JsonValueKind.True => DynValue.True,
            JsonValueKind.False => DynValue.False,
            _ => DynValue.Nil
        };
    }

    private static DynValue JsonObjectToTable(Script script, JsonElement element)
    {
        var table = new Table(script);
        foreach (var property in element.EnumerateObject())
        {
            table[property.Name] = JsonToDynValue(script, property.Value);
        }

        return DynValue.NewTable(table);
    }

    private static DynValue JsonArrayToTable(Script script, JsonElement element)
    {
        var table = new Table(script);
        var index = 1;
        foreach (var value in element.EnumerateArray())
        {
            table[index++] = JsonToDynValue(script, value);
        }

        return DynValue.NewTable(table);
    }

    private static PointCollection PointsFromDynValue(DynValue value)
    {
        if (value.Type != DataType.Table)
        {
            throw new ScriptRuntimeException("polygon points must be a table.");
        }

        var points = new PointCollection();
        var table = value.Table;
        var index = 1;
        while (table.Get(index).Type != DataType.Nil)
        {
            var item = table.Get(index);
            if (item.Type == DataType.Table)
            {
                points.Add(new Point(
                    TableNumber(item.Table, "x", 1, 0),
                    TableNumber(item.Table, "y", 2, 0)));
                index++;
            }
            else
            {
                var x = DynValueToNumber(item, 0);
                var yValue = table.Get(index + 1);
                if (yValue.Type == DataType.Nil)
                {
                    break;
                }

                points.Add(new Point(x, DynValueToNumber(yValue, 0)));
                index += 2;
            }
        }

        if (points.Count < 2)
        {
            throw new ScriptRuntimeException("polygon needs at least two points.");
        }

        return points;
    }

    private static double TableNumber(Table table, string key, int index, double fallback)
    {
        var keyed = table.Get(key);
        if (keyed.Type == DataType.Number)
        {
            return keyed.Number;
        }

        var indexed = table.Get(index);
        return indexed.Type == DataType.Number ? indexed.Number : fallback;
    }

    private static DynValue ArgValue(CallbackArguments args, int index)
    {
        return index >= args.Count ? DynValue.Nil : args[index];
    }

    private static string DynValueToString(DynValue value)
    {
        return value.Type == DataType.String ? value.String : value.ToPrintString();
    }

    private static double DynValueToNumber(DynValue value, double fallback)
    {
        if (value.Type == DataType.Number)
        {
            return value.Number;
        }

        if (value.Type == DataType.String && double.TryParse(value.String, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static string ArgString(CallbackArguments args, int index, string fallback)
    {
        if (index >= args.Count)
        {
            return fallback;
        }

        var value = args[index];
        if (value.IsNil())
        {
            return fallback;
        }

        return value.Type == DataType.String ? value.String : value.ToPrintString();
    }

    private static double ArgDouble(CallbackArguments args, int index, double fallback)
    {
        if (index >= args.Count)
        {
            return fallback;
        }

        var value = args[index];
        if (value.Type == DataType.Number)
        {
            return value.Number;
        }

        if (value.Type == DataType.String && double.TryParse(value.String, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static bool ArgBool(CallbackArguments args, int index, bool fallback)
    {
        if (index >= args.Count)
        {
            return fallback;
        }

        var value = args[index];
        if (value.Type == DataType.Boolean)
        {
            return value.Boolean;
        }

        if (value.Type == DataType.String)
        {
            return value.String.Trim().ToLowerInvariant() switch
            {
                "true" or "yes" or "on" or "enabled" or "1" => true,
                "false" or "no" or "off" or "disabled" or "0" => false,
                _ => fallback
            };
        }

        if (value.Type == DataType.Number)
        {
            return Math.Abs(value.Number) > double.Epsilon;
        }

        return fallback;
    }
}

internal sealed class LuaWidgetWindow : Window
{
    private readonly WidgetRuntime _runtime;
    private readonly Canvas _canvas;
    private readonly LuaImmediateGui _gui;
    private bool _closingInternally;

    internal event Func<double, double, bool>? LeftClicked;

    internal LuaWidgetWindow(WidgetRuntime runtime, string title, int left, int top, int width, int height)
    {
        _runtime = runtime;
        Title = title;
        Left = left;
        Top = top;
        Width = Math.Max(40, width);
        Height = Math.Max(40, height);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;

        _canvas = new Canvas
        {
            Width = Width,
            Height = Height,
            Background = Brushes.Transparent,
            ClipToBounds = true
        };
        _gui = new LuaImmediateGui(_canvas, _runtime.ResolveFontFamily);
        Content = _canvas;

        MouseLeftButtonDown += (_, e) =>
        {
            var position = e.GetPosition(_canvas);
            if (e.ClickCount == 2)
            {
                _runtime.OpenSettings();
            }
            else if (position.Y <= 20)
            {
                WidgetDrag.Begin(this, e, () => { _runtime.SaveConfig(); _runtime.SendWidgetsToDesktop(); });
            }
            else if (LeftClicked?.Invoke(position.X, position.Y) == true)
            {
                e.Handled = true;
            }
            else
            {
                WidgetDrag.Begin(this, e, () => { _runtime.SaveConfig(); _runtime.SendWidgetsToDesktop(); });
            }
        };
        Closing += (_, e) =>
        {
            if (!_closingInternally)
            {
                _runtime.SaveConfig();
            }
        };
        ContextMenu = BuildMenu(runtime);
    }

    internal LuaTextElement AddText(string text, double x, double y, double size, string color, string weight, string fontFamily)
    {
        var element = new TextBlock
        {
            Text = text,
            FontFamily = _runtime.ResolveFontFamily(fontFamily),
            FontSize = size,
            FontWeight = string.Equals(weight, "bold", StringComparison.OrdinalIgnoreCase) ? FontWeights.Bold : FontWeights.Normal,
            Foreground = NewBrush(color, 1),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        _canvas.Children.Add(element);
        return new LuaTextElement(element, _canvas, _runtime.ResolveFontFamily);
    }

    internal LuaRectangleElement AddRectangle(double x, double y, double width, double height, string color, double opacity, double radius, string strokeColor, double strokeThickness)
    {
        var element = new Rectangle
        {
            Width = Math.Max(0, width),
            Height = Math.Max(0, height),
            Fill = NewBrush(color, opacity),
            Stroke = NewBrush(strokeColor, opacity),
            StrokeThickness = Math.Max(0, strokeThickness),
            RadiusX = Math.Max(0, radius),
            RadiusY = Math.Max(0, radius)
        };
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        _canvas.Children.Add(element);
        return new LuaRectangleElement(element, _canvas);
    }

    internal LuaCircleElement AddCircle(double x, double y, double size, string color, double opacity, string strokeColor, double strokeThickness)
    {
        var diameter = Math.Max(0, size);
        var element = new Ellipse
        {
            Width = diameter,
            Height = diameter,
            Fill = NewBrush(color, opacity),
            Stroke = NewBrush(strokeColor, opacity),
            StrokeThickness = Math.Max(0, strokeThickness)
        };
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        _canvas.Children.Add(element);
        return new LuaCircleElement(element, _canvas);
    }

    internal LuaLineElement AddLine(double x1, double y1, double x2, double y2, string color, double thickness, double opacity)
    {
        var element = new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = NewBrush(color, opacity),
            StrokeThickness = Math.Max(0, thickness),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        _canvas.Children.Add(element);
        return new LuaLineElement(element, _canvas);
    }

    internal LuaPolygonElement AddPolygon(PointCollection points, string color, double opacity, string strokeColor, double strokeThickness)
    {
        var element = new Polygon
        {
            Points = points,
            Fill = NewBrush(color, opacity),
            Stroke = NewBrush(strokeColor, opacity),
            StrokeThickness = Math.Max(0, strokeThickness)
        };
        _canvas.Children.Add(element);
        return new LuaPolygonElement(element, _canvas);
    }

    internal LuaArcElement AddArc(double x, double y, double width, double height, double startAngle, double sweepAngle, string color, double thickness, double opacity)
    {
        var element = new Path
        {
            Data = NewArcGeometry(x, y, width, height, startAngle, sweepAngle),
            Stroke = NewBrush(color, opacity),
            StrokeThickness = Math.Max(0, thickness),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = Brushes.Transparent
        };
        _canvas.Children.Add(element);
        return new LuaArcElement(element, _canvas);
    }

    internal LuaImageElement AddImage(string path, double x, double y, double width, double height, double opacity)
    {
        var element = new Image
        {
            Width = Math.Max(0, width),
            Height = Math.Max(0, height),
            Stretch = Stretch.Uniform,
            Opacity = Math.Clamp(opacity, 0, 1)
        };

        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && LuaHttpPolicy.ApprovedHosts.Contains(uri.Host))
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            element.Source = image;
        }
        else if (System.IO.File.Exists(path))
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            element.Source = image;
        }

        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        _canvas.Children.Add(element);
        return new LuaImageElement(element, _canvas);
    }

    internal void BeginGuiFrame() => _gui.BeginFrame();

    internal void GuiPanel(double x, double y, double width, double height, string color, double opacity, double radius, string strokeColor, double strokeThickness)
    {
        _gui.Panel(x, y, width, height, color, opacity, radius, strokeColor, strokeThickness);
    }

    internal void GuiLabel(string text, double x, double y, double size, string color, string weight, string fontFamily)
    {
        _gui.Label(text, x, y, size, color, weight, fontFamily);
    }

    internal bool GuiButton(string text, double x, double y, double width, double height, string key)
    {
        return _gui.Button(text, x, y, width, height, key);
    }

    internal bool GuiIconButton(string icon, double x, double y, double width, double height, string key)
    {
        return _gui.IconButton(icon, x, y, width, height, key);
    }

    internal bool HandleGuiClick(double x, double y) => _gui.HandleClick(x, y);

    internal void Clear()
    {
        _canvas.Children.Clear();
        _gui.BeginFrame();
    }

    internal void SetWidgetSize(double width, double height)
    {
        Width = Math.Max(40, width);
        Height = Math.Max(40, height);
        _canvas.Width = Width;
        _canvas.Height = Height;
    }

    internal void SetBackground(string color, double opacity)
    {
        if (string.Equals(color, "transparent", StringComparison.OrdinalIgnoreCase))
        {
            _canvas.Background = Brushes.Transparent;
            return;
        }

        _canvas.Background = NewBrush(color, opacity);
    }

    internal void CloseWithoutSaving()
    {
        _closingInternally = true;
        Close();
    }

    private static ContextMenu BuildMenu(WidgetRuntime runtime)
    {
        var menu = new ContextMenu();
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

    internal static Brush NewBrush(string color, double opacity)
    {
        var alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        try
        {
            if (TryGradient(color, alpha, out var gradient))
            {
                return gradient;
            }

            if (color.Contains(',', StringComparison.Ordinal))
            {
                var parts = color.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length == 3)
                {
                    return new SolidColorBrush(Color.FromArgb(
                        alpha,
                        byte.Parse(parts[0], CultureInfo.InvariantCulture),
                        byte.Parse(parts[1], CultureInfo.InvariantCulture),
                        byte.Parse(parts[2], CultureInfo.InvariantCulture)));
                }
            }

            var converted = (Color)ColorConverter.ConvertFromString(color)!;
            converted.A = alpha;
            return new SolidColorBrush(converted);
        }
        catch
        {
            return new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));
        }
    }

    internal static Geometry NewArcGeometry(double x, double y, double width, double height, double startAngle, double sweepAngle)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        var center = new Point(x + (width / 2), y + (height / 2));
        var start = PointOnEllipse(center, width / 2, height / 2, startAngle);
        var end = PointOnEllipse(center, width / 2, height / 2, startAngle + sweepAngle);
        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(width / 2, height / 2),
            IsLargeArc = Math.Abs(sweepAngle) > 180,
            SweepDirection = sweepAngle >= 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise
        });
        return new PathGeometry([figure]);
    }

    private static Point PointOnEllipse(Point center, double radiusX, double radiusY, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180;
        return new Point(center.X + (Math.Cos(radians) * radiusX), center.Y + (Math.Sin(radians) * radiusY));
    }

    private static bool TryGradient(string color, byte alpha, out Brush brush)
    {
        brush = Brushes.Transparent;
        var trimmed = color.Trim();
        if (trimmed.StartsWith("linear(", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith(')'))
        {
            var parts = trimmed[7..^1].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                var angle = parts.Length >= 3 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 90;
                var radians = angle * Math.PI / 180;
                var brushStart = new Point(0.5 - (Math.Cos(radians) / 2), 0.5 - (Math.Sin(radians) / 2));
                var brushEnd = new Point(0.5 + (Math.Cos(radians) / 2), 0.5 + (Math.Sin(radians) / 2));
                brush = new LinearGradientBrush(NewColor(parts[0], alpha), NewColor(parts[1], alpha), brushStart, brushEnd);
                return true;
            }
        }

        if (trimmed.StartsWith("radial(", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith(')'))
        {
            var parts = trimmed[7..^1].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                brush = new RadialGradientBrush(NewColor(parts[0], alpha), NewColor(parts[1], alpha));
                return true;
            }
        }

        return false;
    }

    private static Color NewColor(string color, byte alpha)
    {
        try
        {
            var converted = (Color)ColorConverter.ConvertFromString(color.Trim())!;
            converted.A = alpha;
            return converted;
        }
        catch
        {
            return Color.FromArgb(alpha, 255, 255, 255);
        }
    }
}

[MoonSharpUserData]
public abstract class LuaVisualElement
{
    private readonly UIElement _element;
    private readonly Canvas _parent;

    protected LuaVisualElement(UIElement element, Canvas parent)
    {
        _element = element;
        _parent = parent;
    }

    public void set_position(double x, double y)
    {
        Canvas.SetLeft(_element, x);
        Canvas.SetTop(_element, y);
    }

    public void set_opacity(double opacity)
    {
        _element.Opacity = Math.Clamp(opacity, 0, 1);
    }

    public void remove()
    {
        _parent.Children.Remove(_element);
    }
}

[MoonSharpUserData]
public sealed class LuaTextElement : LuaVisualElement
{
    private readonly TextBlock _text;
    private readonly Func<string, FontFamily> _fontResolver;

    internal LuaTextElement(TextBlock text, Canvas parent, Func<string, FontFamily> fontResolver)
        : base(text, parent)
    {
        _text = text;
        _fontResolver = fontResolver;
    }

    public void set_text(string text)
    {
        _text.Text = text;
    }

    public void set_color(string color, double opacity = 1)
    {
        _text.Foreground = LuaWidgetWindow.NewBrush(color, opacity);
    }

    public void set_font_size(double size)
    {
        _text.FontSize = Math.Max(1, size);
    }

    public void set_font(string font)
    {
        _text.FontFamily = _fontResolver(font);
    }

    public void set_width(double width, bool wrap = true)
    {
        _text.Width = Math.Max(0, width);
        _text.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _text.TextTrimming = wrap ? TextTrimming.CharacterEllipsis : TextTrimming.None;
    }
}

[MoonSharpUserData]
public sealed class LuaRectangleElement : LuaVisualElement
{
    private readonly Rectangle _rectangle;

    internal LuaRectangleElement(Rectangle rectangle, Canvas parent)
        : base(rectangle, parent)
    {
        _rectangle = rectangle;
    }

    public void set_size(double width, double height)
    {
        _rectangle.Width = Math.Max(0, width);
        _rectangle.Height = Math.Max(0, height);
    }

    public void set_color(string color, double opacity = 1)
    {
        _rectangle.Fill = LuaWidgetWindow.NewBrush(color, opacity);
    }

    public void set_stroke(string color, double thickness, double opacity = 1)
    {
        _rectangle.Stroke = LuaWidgetWindow.NewBrush(color, opacity);
        _rectangle.StrokeThickness = Math.Max(0, thickness);
    }
}

[MoonSharpUserData]
public sealed class LuaCircleElement : LuaVisualElement
{
    private readonly Ellipse _circle;

    internal LuaCircleElement(Ellipse circle, Canvas parent)
        : base(circle, parent)
    {
        _circle = circle;
    }

    public void set_size(double size)
    {
        var diameter = Math.Max(0, size);
        _circle.Width = diameter;
        _circle.Height = diameter;
    }

    public void set_color(string color, double opacity = 1)
    {
        _circle.Fill = LuaWidgetWindow.NewBrush(color, opacity);
    }

    public void set_stroke(string color, double thickness, double opacity = 1)
    {
        _circle.Stroke = LuaWidgetWindow.NewBrush(color, opacity);
        _circle.StrokeThickness = Math.Max(0, thickness);
    }
}

[MoonSharpUserData]
public sealed class LuaLineElement : LuaVisualElement
{
    private readonly Line _line;

    internal LuaLineElement(Line line, Canvas parent)
        : base(line, parent)
    {
        _line = line;
    }

    public void set_points(double x1, double y1, double x2, double y2)
    {
        _line.X1 = x1;
        _line.Y1 = y1;
        _line.X2 = x2;
        _line.Y2 = y2;
    }

    public void set_color(string color, double opacity = 1)
    {
        _line.Stroke = LuaWidgetWindow.NewBrush(color, opacity);
    }

    public void set_thickness(double thickness)
    {
        _line.StrokeThickness = Math.Max(0, thickness);
    }
}

[MoonSharpUserData]
public sealed class LuaPolygonElement : LuaVisualElement
{
    private readonly Polygon _polygon;

    internal LuaPolygonElement(Polygon polygon, Canvas parent)
        : base(polygon, parent)
    {
        _polygon = polygon;
    }

    public void set_color(string color, double opacity = 1)
    {
        _polygon.Fill = LuaWidgetWindow.NewBrush(color, opacity);
    }

    public void set_stroke(string color, double thickness, double opacity = 1)
    {
        _polygon.Stroke = LuaWidgetWindow.NewBrush(color, opacity);
        _polygon.StrokeThickness = Math.Max(0, thickness);
    }
}

[MoonSharpUserData]
public sealed class LuaArcElement : LuaVisualElement
{
    private readonly Path _path;

    internal LuaArcElement(Path path, Canvas parent)
        : base(path, parent)
    {
        _path = path;
    }

    public void set_arc(double x, double y, double width, double height, double start_angle, double sweep_angle)
    {
        _path.Data = LuaWidgetWindow.NewArcGeometry(x, y, width, height, start_angle, sweep_angle);
    }

    public void set_color(string color, double opacity = 1)
    {
        _path.Stroke = LuaWidgetWindow.NewBrush(color, opacity);
    }

    public void set_thickness(double thickness)
    {
        _path.StrokeThickness = Math.Max(0, thickness);
    }
}

[MoonSharpUserData]
public sealed class LuaTimerHandle
{
    private readonly DispatcherTimer _timer;
    private readonly Action _onCancel;

    internal LuaTimerHandle(DispatcherTimer timer, Action onCancel)
    {
        _timer = timer;
        _onCancel = onCancel;
    }

    public void cancel()
    {
        _timer.Stop();
        _onCancel();
    }
}

[MoonSharpUserData]
public sealed class LuaImageElement : LuaVisualElement
{
    private readonly Image _image;

    internal LuaImageElement(Image image, Canvas parent)
        : base(image, parent)
    {
        _image = image;
    }

    public void set_size(double width, double height)
    {
        _image.Width = Math.Max(0, width);
        _image.Height = Math.Max(0, height);
    }
}

internal sealed class LuaWidgetManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Script { get; set; } = "main.lua";
    public int Width { get; set; } = 220;
    public int Height { get; set; } = 90;
    public int Left { get; set; } = 80;
    public int Top { get; set; } = 460;
    public double UpdateIntervalSeconds { get; set; } = 1;
    public bool Enabled { get; set; } = true;

    internal static LuaWidgetManifest Load(string path)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        var manifest = JsonSerializer.Deserialize<LuaWidgetManifest>(System.IO.File.ReadAllText(path), options)
            ?? new LuaWidgetManifest();
        manifest.Width = Math.Max(40, manifest.Width);
        manifest.Height = Math.Max(40, manifest.Height);
        manifest.UpdateIntervalSeconds = Math.Clamp(manifest.UpdateIntervalSeconds, 0.05, 86400);
        if (string.IsNullOrWhiteSpace(manifest.Script))
        {
            manifest.Script = "main.lua";
        }

        return manifest;
    }

    internal static LuaWidgetManifest ErrorFallback(string id) => new()
    {
        Id = id,
        Name = id,
        Width = 260,
        Height = 90
    };

    internal string NameOrId(string id) => string.IsNullOrWhiteSpace(Name) ? id : Name;
}
