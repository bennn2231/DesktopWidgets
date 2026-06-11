# Widges Desktop (.NET)

This folder contains a C#/.NET WPF rewrite of the desktop widget.

## Build and Run

```powershell
dotnet build .\DesktopWidgetDotNet.sln
dotnet run --project .\WidgesDesktopDotNet.csproj
```

``` for .exe
dotnet publish .\WidgesDesktopDotNet.csproj -c Release -r win-x64 --self-contained false
```

``` for .exe with dependencies
dotnet publish .\WidgesDesktopDotNet.csproj -c Release --no-restore
```
The app reads and writes:

- `DesktopWidget/config.json`
- `@Resources/WeatherIcons/*.png`
- `Widger/Lua/*/widget.json`
- `Widger/Lua/**/*.lua`

so it stays aligned with your current skin assets and settings.

## Spotify Widget

The Spotify widget shows the currently playing track with album art, scrolling title, artist name, and playback controls (previous / play-pause / next). It uses the Spotify Web API with OAuth 2.0 PKCE — no client secret is required.

### 1. Create a Spotify Developer App

1. Go to [https://developer.spotify.com/dashboard](https://developer.spotify.com/dashboard) and log in.
2. Click **Create app**.
3. Fill in any name and description.
4. Under **Redirect URIs**, add exactly: `http://127.0.0.1:8765/callback/`
5. Check **Web API** under APIs used, then click **Save**.
6. Open the app and copy the **Client ID** from the app overview page.

### 2. Configure in Widges

1. Run the app (`dotnet run` or the published `.exe`).
2. Double-click the Spotify widget **or** right-click anywhere → **Settings**.
3. In the sidebar click **Spotify**.
4. Set **Spotify widget** to **Enabled**.
5. Paste your **Client ID** into the Client ID field.
6. Leave **Redirect URI** as the default (`http://127.0.0.1:8765/callback/`) unless you changed it in the developer dashboard.
7. Click **Save**.

### 3. Log in

Click the **play button** on the widget — Widges opens your browser to the Spotify authorization page. Approve access, the browser shows "Spotify connected", and the widget starts displaying the current track.

The token is saved to `DesktopWidget/spotify-auth.json` and refreshed automatically, so you only need to log in once.

### Troubleshooting

| Symptom | Fix |
|---|---|
| "Paste your Spotify Client ID first." | Client ID field is empty — go to Settings → Spotify. |
| "INVALID_CLIENT" in the browser | Client ID is wrong, or the redirect URI in the dashboard doesn't match exactly (include the trailing `/`). |
| Widget shows "Not connected" after login | The login browser tab may have timed out (3-minute window). Click play again to retry. |
| Controls do nothing | Spotify must have an active device (desktop app, web player, phone). Open Spotify and play something first. |

To force a data refresh at any time, right-click the widget and choose **Refresh Spotify**.

## Lua Widgets

Lua scripting is powered by MoonSharp. See `LUA_WIDGETS.md` for the widget folder format, API, and an example. A starter clock widget lives in `examples/lua-clock`.
