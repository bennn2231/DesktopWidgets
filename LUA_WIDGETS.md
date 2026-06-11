# Lua widgets

Custom Lua widgets live in:

```text
Widger/Lua/<widget-id>/
  widget.json
  main.lua
```

The default folder can be changed in `Settings > Lua Widgets`. 

Copy `examples/lua-clock` or `examples/stick-runner` into that folder, then save settings or right-click any widget and choose `Reload Lua Widgets`.

Open `Settings > Lua Widgets` to use the built-in editor. It lists `.lua` files under the selected `Load from` folder, saves the selected script, checks Lua syntax before writing, loads a widget by enabling or creating its `widget.json`, and unloads a widget by disabling it.

## widget.json

```json
{
  "id": "lua-clock",
  "name": "Lua Clock",
  "script": "main.lua",
  "width": 260,
  "height": 92,
  "left": 80,
  "top": 460,
  "updateIntervalSeconds": 1,
  "enabled": true
}
```

Positions are saved into `DesktopWidget/config.json` after dragging a widget, so editing `left` and `top` later only affects first launch or widgets without saved state. `updateIntervalSeconds` can use decimals, such as `0.08`, for smoother animation.

## Lua lifecycle

```lua
function on_load()
  -- runs once when the widget is loaded
end

function on_update()
  -- runs every updateIntervalSeconds
end

function on_click(x, y)
  -- optional left-click handler. x and y are widget-local coordinates.
end

function on_close()
  -- optional cleanup hook
end
```

## Drawing API

```lua
local label = widget.text("Hello", x, y, font_size, color, weight, font)
local box = widget.rect(x, y, width, height, color, opacity, radius, stroke_color, stroke_width)
local dot = widget.circle(x, y, size, color, opacity, stroke_color, stroke_width)
local line = widget.line(x1, y1, x2, y2, color, thickness, opacity)
local poly = widget.polygon({ {10, 10}, {80, 20}, {40, 70} }, color, opacity, stroke_color, stroke_width)
local arc = widget.arc(x, y, width, height, start_angle, sweep_angle, color, thickness, opacity)
local img = widget.image("image.png", x, y, width, height, opacity)

local once = widget.after(500, function()
  widget.log("runs once after 500ms")
end)

local loop = widget.every(1000, function()
  widget.log("runs every second")
end)
loop:cancel()

widget.clear()
widget.set_size(width, height)
widget.set_background(color, opacity)
widget.log("message")
widget.now("HH:mm:ss")

local name = widget.setting_text("name", "Display name", "Desk")
local speed = widget.setting_slider("speed", "Animation speed", 0, 10, 3, 1)
local enabled = widget.setting_bool("enabled", "Enabled", true)
local mode = widget.setting_choice("mode", "Mode", "compact|full", "compact")
local last_click = widget.setting_button("refresh", "Refresh now")
```

Colors can be `"#RRGGBB"` or `"r,g,b"`. Opacity is `0` to `1`. Gradients can be used for fills with strings like `"linear(#111827,#2563eb,90)"` or `"radial(#ffffff,#2563eb)"`. The optional text `font` is a Windows font family name, such as `"Consolas"` or `"Segoe UI"`, or a font family from `@Resources/Fonts`, such as `"Montserrat Light"`.

Lua setting declarations create real controls in `Settings > Lua Widgets` when that script is selected:

`widget.setting_text(key, label, default)` creates a text box.
`widget.setting_slider(key, label, min, max, default, step)` creates a slider.
`widget.setting_bool(key, label, default)` creates an Enabled/Disabled dropdown and returns a boolean.
`widget.setting_choice(key, label, "a|b|c", default)` creates a dropdown and returns the selected string.
`widget.setting_button(key, label)` creates a button and returns the last click token string. Use it as a simple action signal after saving settings.

Settings are saved into `DesktopWidget/config.json`, falling back to the default when no value has been saved yet.

## Immediate UI API

Lua also has a small ImGui-style layer at `widget.ui`. It redraws each frame and returns `true` for buttons that were clicked since the last draw:

```lua
function on_update()
  local ui = widget.ui
  ui.begin()
  ui.panel(0, 0, 240, 120, "#0d0d0d", 0.94, 14, "156,163,175", 1)
  ui.label("Immediate UI", 16, 13, 16, "#ffffff", "bold")

  if ui.button("Count", 16, 76, 72, 28, "count") then
    clicks = clicks + 1
  end

  if ui.icon_button("play", 104, 76, 36, 28, "play") then
    playing = not playing
  end

  ui.end_frame()
end
```

Available icons are `play`, `pause`, `previous`, `next`, `refresh`, and `settings`. The final argument on buttons is a stable id; keep it unique inside the widget. A disabled example lives in `Widger/Lua/gui-demo`.

## HTTP and JSON

Lua has a small approved-host HTTP bridge and JSON parser:

```lua
local raw = http.get("https://api.open-meteo.com/v1/forecast?latitude=40.58&longitude=-105.08&current=temperature_2m")
local data = json.parse(raw)

local github = http.get_json("https://api.github.com/repos/octocat/Hello-World")
local empty = http.put("https://api.spotify.com/v1/me/player/pause", {
  Authorization = "Bearer " .. token
})
local hosts = http.approved_hosts()
```

`http.get`, `http.get_json`, `http.put`, and `http.post` accept an optional headers table as the second argument. `http.put` and `http.post` accept an optional JSON body string as the third argument.

### Non-blocking HTTP

The synchronous HTTP calls above run on the UI thread when called inside `on_update()` or `on_click()`. For widgets that fetch data, prefer these non-blocking helpers:

```lua
-- Fetches in the background and invokes callback on the UI thread.
-- callback signature: function(ok, textOrError)
http.get_async("https://api.open-meteo.com/v1/forecast?...", function(ok, text)
  if ok then
    local data = json.parse(text)
    -- update retained elements here (widget.text/rect/etc) or set state for next on_update
  else
    widget.log("http failed: " .. text)
  end
end)

-- Like get_async, but parses JSON and passes the parsed Lua value directly.
-- callback signature: function(ok, valueOrError)
http.get_json_async("https://api.github.com/repos/octocat/Hello-World", function(ok, data)
  if ok then
    widget.log("stars: " .. tostring(data.stargazers_count))
  else
    widget.log("json fetch failed: " .. tostring(data))
  end
end)

-- Returns cached text immediately (never blocks). If missing/expired, refreshes in the background.
-- ttlSeconds defaults to 60.
local raw = http.get_cached("https://api.open-meteo.com/v1/forecast?...", nil, 60, "")
```

Approved HTTP hosts are `api.open-meteo.com`, `geocoding-api.open-meteo.com`, `api.github.com`, `raw.githubusercontent.com`, `api.spotify.com`, `accounts.spotify.com`, `i.scdn.co`, `api.robinhood.com`, `nummus.robinhood.com`, `trading.robinhood.com`, `status.robinhood.com`, `rpc.testnet.chain.robinhood.com`, `sequencer.testnet.chain.robinhood.com`, and `explorer.testnet.chain.robinhood.com`. Responses are limited to 1 MB. `widget.image(...)` can also load HTTPS images from approved hosts.

Robinhood note: `trading.robinhood.com` is the official Robinhood Crypto Trading API host. `api.robinhood.com` and `nummus.robinhood.com` are commonly used by unofficial/older Robinhood clients and can change without notice. Private account or trading calls still need whatever authentication Robinhood requires; the Lua bridge only allows the HTTPS request to be made.

## Spotify bridge

The shipped Spotify controller is now a hardcoded widget under `Spotify/`, but Lua widgets can still use the built-in Spotify bridge:

```lua
spotify.configure(client_id, "http://127.0.0.1:8765/callback/")
spotify.login()

local track = spotify.current()
if track.connected and track.ok then
  widget.log(track.name .. " - " .. track.artist)
end

spotify.play()
spotify.pause()
spotify.next()
spotify.previous()
spotify.logout()
```

## Element methods

All elements:

```lua
element:set_position(x, y)
element:set_opacity(0.75)
element:remove()
```

Text:

```lua
label:set_text("Updated")
label:set_color("#ffffff", 1)
label:set_font_size(24)
label:set_font("Consolas")
label:set_width(220)
```

Rectangles, circles, and images:

```lua
box:set_size(100, 40)
box:set_color("#ffcc00", 0.8)
box:set_stroke("#ffffff", 2, 1)
dot:set_size(24)
dot:set_color("#7dd3fc", 1)
dot:set_stroke("#ffffff", 1, 1)
line:set_points(0, 0, 100, 40)
line:set_color("#ffffff", 1)
line:set_thickness(2)
poly:set_stroke("#ffffff", 2, 1)
arc:set_arc(10, 10, 80, 80, 0, 270)
img:set_size(48, 48)
```
