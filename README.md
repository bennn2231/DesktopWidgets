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

## Lua Widgets

Lua scripting is powered by MoonSharp. See `LUA_WIDGETS.md` for the widget folder format, API, and an example. A starter clock widget lives in `examples/lua-clock`.
