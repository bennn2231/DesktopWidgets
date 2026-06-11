using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WidgesDesktopDotNet;

internal sealed class SpotifyService : IDisposable
{
    private const string DefaultRedirectUri = "http://127.0.0.1:8765/callback/";
    private const string Scope = "user-read-currently-playing user-read-playback-state user-modify-playback-state";

    private readonly string _statePath;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly object _gate = new();
    private SpotifyAuthState _state;
    private Task? _loginTask;

    internal SpotifyService(string skinRoot)
    {
        _statePath = System.IO.Path.Combine(skinRoot, "DesktopWidget", "spotify-auth.json");
        _state = LoadState(_statePath);
    }

    internal bool IsConfigured => !string.IsNullOrWhiteSpace(_state.ClientId);

    internal bool IsConnected => !string.IsNullOrWhiteSpace(_state.RefreshToken) || HasUsableAccessToken();

    internal string ClientId
    {
        get
        {
            lock (_gate)
            {
                return _state.ClientId;
            }
        }
    }

    internal string RedirectUri
    {
        get
        {
            lock (_gate)
            {
                return _state.RedirectUri;
            }
        }
    }

    internal string LastStatus
    {
        get
        {
            lock (_gate)
            {
                return _state.LastStatus;
            }
        }
    }

    internal string LastError
    {
        get
        {
            lock (_gate)
            {
                return _state.LastError;
            }
        }
    }

    internal string Configure(string clientId, string? redirectUri)
    {
        clientId = clientId.Trim();
        if (string.IsNullOrWhiteSpace(clientId) || string.Equals(clientId, "PASTE_CLIENT_ID_HERE", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Paste your Spotify Client ID first.");
        }

        var normalizedRedirectUri = NormalizeRedirectUri(redirectUri);
        lock (_gate)
        {
            if (!string.Equals(_state.ClientId, clientId, StringComparison.Ordinal)
                || !string.Equals(_state.RedirectUri, normalizedRedirectUri, StringComparison.Ordinal))
            {
                _state.AccessToken = string.Empty;
                _state.RefreshToken = string.Empty;
                _state.ExpiresAtUtc = DateTimeOffset.MinValue;
            }

            _state.ClientId = clientId;
            _state.RedirectUri = normalizedRedirectUri;
            _state.LastStatus = "configured";
            _state.LastError = string.Empty;
            SaveState();
        }

        return "configured";
    }

    internal string Login()
    {
        lock (_gate)
        {
            if (!IsConfigured)
            {
                return "Paste your Spotify Client ID first.";
            }

            if (_loginTask is { IsCompleted: false })
            {
                return "Spotify login is already open.";
            }

            _state.LastStatus = "login_started";
            _state.LastError = string.Empty;
            SaveState();
            _loginTask = Task.Run(RunLoginAsync);
            return "Opened Spotify login in your browser.";
        }
    }

    internal string Logout()
    {
        lock (_gate)
        {
            _state.AccessToken = string.Empty;
            _state.RefreshToken = string.Empty;
            _state.ExpiresAtUtc = DateTimeOffset.MinValue;
            _state.LastStatus = "logged_out";
            _state.LastError = string.Empty;
            SaveState();
        }

        return "logged_out";
    }

    internal SpotifyCurrentTrack Current()
    {
        try
        {
            if (!IsConfigured)
            {
                return SpotifyCurrentTrack.NotConnected("not_configured", "Paste your Spotify Client ID.");
            }

            var token = EnsureAccessToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                return SpotifyCurrentTrack.NotConnected("not_connected", "Click Login.");
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://api.spotify.com/v1/me/player/currently-playing?additional_types=track,episode");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = _httpClient.Send(request);

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                SetStatus("nothing_playing", string.Empty);
                return new SpotifyCurrentTrack(true, true, false, string.Empty, string.Empty, string.Empty, "nothing_playing", string.Empty);
            }

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                SetStatus("api_error", $"Spotify API returned {(int)response.StatusCode}.");
                return SpotifyCurrentTrack.NotConnected("api_error", $"Spotify API returned {(int)response.StatusCode}.");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("item", out var item) || item.ValueKind == JsonValueKind.Null)
            {
                SetStatus("nothing_playing", string.Empty);
                return new SpotifyCurrentTrack(true, true, false, string.Empty, string.Empty, string.Empty, "nothing_playing", string.Empty);
            }

            var isPlaying = root.TryGetProperty("is_playing", out var playingElement) && playingElement.GetBoolean();
            var title = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
            var artist = ArtistName(item);
            var image = ImageUrl(item);
            SetStatus(isPlaying ? "playing" : "paused", string.Empty);
            return new SpotifyCurrentTrack(true, true, isPlaying, title, artist, image, isPlaying ? "playing" : "paused", string.Empty);
        }
        catch (Exception ex)
        {
            SetStatus("error", ex.Message);
            return SpotifyCurrentTrack.NotConnected("error", ex.Message);
        }
    }

    internal bool Play() => SendPlayerCommand(HttpMethod.Put, "play");

    internal bool Pause() => SendPlayerCommand(HttpMethod.Put, "pause");

    internal bool Next() => SendPlayerCommand(HttpMethod.Post, "next");

    internal bool Previous() => SendPlayerCommand(HttpMethod.Post, "previous");

    public void Dispose() => _httpClient.Dispose();

    private async Task RunLoginAsync()
    {
        string clientId;
        string redirectUriText;
        lock (_gate)
        {
            clientId = _state.ClientId;
            redirectUriText = _state.RedirectUri;
        }

        try
        {
            var redirectUri = new Uri(redirectUriText);
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state = Base64Url(RandomNumberGenerator.GetBytes(18));

            using var listener = new TcpListener(IPAddress.Loopback, redirectUri.Port);
            listener.Start();

            var authUrl = BuildAuthorizeUrl(clientId, redirectUriText, challenge, state);
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            var requestLine = await ReadRequestLineAsync(client, timeout.Token);
            var query = ParseCallbackQuery(requestLine, redirectUri.AbsolutePath);
            await WriteBrowserResponseAsync(client, query.TryGetValue("error", out var callbackError) ? callbackError : string.Empty, timeout.Token);

            if (!query.TryGetValue("state", out var returnedState) || returnedState != state)
            {
                throw new InvalidOperationException("Spotify login state did not match.");
            }

            if (query.TryGetValue("error", out var error) && !string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException($"Spotify login failed: {error}");
            }

            if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            {
                throw new InvalidOperationException("Spotify did not return an authorization code.");
            }

            await ExchangeCodeAsync(clientId, redirectUriText, code, verifier);
            SetStatus("connected", string.Empty);
        }
        catch (Exception ex)
        {
            SetStatus("login_error", ex.Message);
        }
    }

    private string EnsureAccessToken()
    {
        lock (_gate)
        {
            if (HasUsableAccessToken())
            {
                return _state.AccessToken;
            }
        }

        return RefreshAccessToken();
    }

    private string RefreshAccessToken()
    {
        string clientId;
        string refreshToken;
        lock (_gate)
        {
            clientId = _state.ClientId;
            refreshToken = _state.RefreshToken;
        }

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(refreshToken))
        {
            return string.Empty;
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        });
        using var response = _httpClient.PostAsync("https://accounts.spotify.com/api/token", content).GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            SetStatus("refresh_error", $"Spotify token refresh returned {(int)response.StatusCode}.");
            return string.Empty;
        }

        using var doc = JsonDocument.Parse(body);
        lock (_gate)
        {
            _state.AccessToken = doc.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
            if (doc.RootElement.TryGetProperty("refresh_token", out var newRefresh) && newRefresh.ValueKind == JsonValueKind.String)
            {
                _state.RefreshToken = newRefresh.GetString() ?? _state.RefreshToken;
            }

            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var expiresElement)
                ? expiresElement.GetInt32()
                : 3600;
            _state.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn));
            _state.LastStatus = "connected";
            _state.LastError = string.Empty;
            SaveState();
            return _state.AccessToken;
        }
    }

    private async Task ExchangeCodeAsync(string clientId, string redirectUri, string code, string verifier)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier
        });
        using var response = await _httpClient.PostAsync("https://accounts.spotify.com/api/token", content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Spotify token request returned {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(body);
        lock (_gate)
        {
            _state.AccessToken = doc.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
            _state.RefreshToken = doc.RootElement.GetProperty("refresh_token").GetString() ?? _state.RefreshToken;
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var expiresElement)
                ? expiresElement.GetInt32()
                : 3600;
            _state.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn));
            _state.LastStatus = "connected";
            _state.LastError = string.Empty;
            SaveState();
        }
    }

    private bool SendPlayerCommand(HttpMethod method, string path)
    {
        try
        {
            var token = EnsureAccessToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                SetStatus("not_connected", "Click Login.");
                return false;
            }

            using var request = new HttpRequestMessage(method, $"https://api.spotify.com/v1/me/player/{path}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = _httpClient.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                SetStatus("command_error", SpotifyErrorMessage(response, $"Spotify command returned {(int)response.StatusCode}."));
                return false;
            }

            SetStatus("command_sent", string.Empty);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("command_error", ex.Message);
            return false;
        }
    }

    private bool HasUsableAccessToken()
    {
        return !string.IsNullOrWhiteSpace(_state.AccessToken)
            && _state.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1);
    }

    private void SetStatus(string status, string error)
    {
        lock (_gate)
        {
            _state.LastStatus = status;
            _state.LastError = error;
            SaveState();
        }
    }

    private static string SpotifyErrorMessage(HttpResponseMessage response, string fallback)
    {
        try
        {
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(body))
            {
                return fallback;
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message))
                {
                    var text = message.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }

                if (error.ValueKind == JsonValueKind.String)
                {
                    var text = error.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
        }
        catch
        {
            // Keep the simple HTTP status fallback.
        }

        return fallback;
    }

    private void SaveState()
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_statePath)!);
        var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        System.IO.File.WriteAllText(_statePath, json);
    }

    private static SpotifyAuthState LoadState(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return new SpotifyAuthState();
        }

        try
        {
            var state = JsonSerializer.Deserialize<SpotifyAuthState>(System.IO.File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return state ?? new SpotifyAuthState();
        }
        catch
        {
            return new SpotifyAuthState();
        }
    }

    private static string NormalizeRedirectUri(string? redirectUri)
    {
        var value = string.IsNullOrWhiteSpace(redirectUri) ? DefaultRedirectUri : redirectUri.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || uri.Port <= 0
            || !IPAddress.TryParse(uri.Host, out var address)
            || !IPAddress.IsLoopback(address))
        {
            throw new InvalidOperationException("Redirect URI must be an http loopback IP URL, like http://127.0.0.1:8765/callback/.");
        }

        return value;
    }

    private static string BuildAuthorizeUrl(string clientId, string redirectUri, string challenge, string state)
    {
        var parts = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = Scope,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = challenge,
            ["state"] = state
        };
        return "https://accounts.spotify.com/authorize?" + string.Join("&", parts.Select(x =>
            $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
    }

    private static async Task<string> ReadRequestLineAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
        return await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
    }

    private static Dictionary<string, string> ParseCallbackQuery(string requestLine, string expectedPath)
    {
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new InvalidOperationException("Invalid Spotify callback.");
        }

        var uri = new Uri("http://127.0.0.1" + parts[1]);
        var requestPath = uri.AbsolutePath.TrimEnd('/');
        var wantedPath = expectedPath.TrimEnd('/');
        if (!string.Equals(requestPath, wantedPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Spotify callback path did not match the redirect URI.");
        }

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace("+", " "));
            var value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace("+", " ")) : string.Empty;
            query[key] = value;
        }

        return query;
    }

    private static async Task WriteBrowserResponseAsync(TcpClient client, string error, CancellationToken cancellationToken)
    {
        var title = string.IsNullOrWhiteSpace(error) ? "Spotify connected" : "Spotify login failed";
        var detail = string.IsNullOrWhiteSpace(error)
            ? "You can close this tab and go back to Widges."
            : WebUtility.HtmlEncode(error);
        var html = $"<!doctype html><title>{title}</title><body style=\"font-family:Segoe UI,sans-serif;background:#111827;color:#f8fafc;padding:32px\"><h1>{title}</h1><p>{detail}</p></body>";
        var body = Encoding.UTF8.GetBytes(html);
        var headers = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n" +
            "Connection: close\r\n\r\n");
        var stream = client.GetStream();
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    private static string ArtistName(JsonElement item)
    {
        if (item.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            var names = artists.EnumerateArray()
                .Select(x => x.TryGetProperty("name", out var name) ? name.GetString() : null)
                .Where(x => !string.IsNullOrWhiteSpace(x));
            return string.Join(", ", names);
        }

        if (item.TryGetProperty("show", out var show) && show.TryGetProperty("name", out var showName))
        {
            return showName.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string ImageUrl(JsonElement item)
    {
        if (item.TryGetProperty("album", out var album)
            && album.TryGetProperty("images", out var albumImages)
            && albumImages.ValueKind == JsonValueKind.Array
            && albumImages.GetArrayLength() > 0
            && albumImages[0].TryGetProperty("url", out var albumUrl))
        {
            return albumUrl.GetString() ?? string.Empty;
        }

        if (item.TryGetProperty("images", out var images)
            && images.ValueKind == JsonValueKind.Array
            && images.GetArrayLength() > 0
            && images[0].TryGetProperty("url", out var url))
        {
            return url.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

internal sealed class SpotifyAuthState
{
    public string ClientId { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = "http://127.0.0.1:8765/callback/";
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.MinValue;
    public string LastStatus { get; set; } = "not_configured";
    public string LastError { get; set; } = string.Empty;
}

internal sealed record SpotifyCurrentTrack(
    bool Ok,
    bool Connected,
    bool IsPlaying,
    string Name,
    string Artist,
    string AlbumImageUrl,
    string Status,
    string Error)
{
    internal static SpotifyCurrentTrack NotConnected(string status, string error)
        => new(false, false, false, string.Empty, string.Empty, string.Empty, status, error);
}
