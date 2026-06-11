-- Robinhood-style desktop widget.
--
-- Settings in the Lua editor:
--   mode: "today" or "stocks"
--   bearer_token: an existing Robinhood web/app bearer token for read-only account data
--   demo_mode: enabled to use sample data, disabled to call Robinhood
--   symbols: fallback comma-separated symbols for the stocks card
--
-- This script only makes GET requests. It does not place trades.

local GREEN = "#00c805"
local RED = "#ff5000"
local BLACK = "#111111"
local MUTED = "#8a8a8a"
local CARD = "#f7f7f7"
local LINE = "#e8e8e8"

local mode = "today"
local bearer_token = ""
local demo_mode = true
local symbol_setting = "POWA,ACOT,PTG,HKTP,SHB"
local update_count = 0
local state = {
  ok = false,
  error = "",
  change_percent = 1.44,
  updated = "10 sec ago",
  stocks = {
    { symbol = "POWA", shares = "2 shares", price = "$234.55", color = GREEN, seed = 0.5, trend = 0.24 },
    { symbol = "ACOT", shares = "1 share", price = "$32.15", color = GREEN, seed = 2.3, trend = 0.12 },
    { symbol = "PTG", shares = "1 share", price = "$132.22", color = GREEN, seed = 4.2, trend = 0.18 },
    { symbol = "HKTP", shares = "1 share", price = "$2.51", color = RED, seed = 7.1, trend = -0.28 },
    { symbol = "SHB", shares = "1 share", price = "$302.98", color = GREEN, seed = 8.6, trend = 0.08 }
  }
}

local function lower(value)
  return string.lower(value or "")
end

local function text(value, x, y, size, color, weight)
  return widget.text(value, x, y, size, color or BLACK, weight or "normal", "Segoe UI")
end

local function rect(x, y, w, h, color, opacity, radius, stroke, stroke_width)
  return widget.rect(x, y, w, h, color, opacity or 1, radius or 0, stroke or "transparent", stroke_width or 0)
end

local function dollars(value)
  local number = tonumber(value or 0) or 0
  return "$" .. string.format("%.2f", number)
end

local function percent(value)
  local number = tonumber(value or 0) or 0
  return string.format("%.2f%%", math.abs(number))
end

local function compact_shares(quantity)
  local number = tonumber(quantity or 0) or 0
  local rounded = math.floor(number * 100 + 0.5) / 100
  if math.abs(rounded - math.floor(rounded)) < 0.001 then
    rounded = math.floor(rounded)
  end
  local label = tostring(rounded)
  if rounded == 1 then
    return label .. " share"
  end
  return label .. " shares"
end

local function split_symbols(value)
  local result = {}
  for symbol in string.gmatch(value or "", "([^,%s]+)") do
    result[#result + 1] = string.upper(symbol)
  end
  return result
end

local function headers()
  local token = bearer_token or ""
  if token == "" then
    return {}
  end
  if string.sub(lower(token), 1, 7) == "bearer " then
    return { Authorization = token }
  end
  return { Authorization = "Bearer " .. token }
end

local function robinhood_mark(x, y, scale)
  local s = scale or 1
  widget.polygon({
    { x + 0 * s, y + 16 * s },
    { x + 11 * s, y + 3 * s },
    { x + 22 * s, y + 0 * s },
    { x + 16 * s, y + 7 * s },
    { x + 21 * s, y + 7 * s },
    { x + 11 * s, y + 22 * s },
    { x + 10 * s, y + 12 * s }
  }, "#050505", 1)
  widget.line(x + 12 * s, y + 5 * s, x + 7 * s, y + 18 * s, "#f7f7f7", 1.2 * s, 1)
end

local function sparkline(x, y, w, h, color, seed, trend)
  local count = 29
  local last_x = x
  local last_y = y + h * 0.55

  for i = 1, count do
    local t = (i - 1) / (count - 1)
    local wiggle = math.sin((t * 9.0) + seed) * 0.18 + math.sin((t * 23.0) + seed * 0.7) * 0.08
    local slope = (trend or 0.15) * (t - 0.5)
    local value = 0.5 - wiggle - slope
    local px = x + (t * w)
    local py = y + math.max(0.08, math.min(0.92, value)) * h

    if i > 1 then
      widget.line(last_x, last_y, px, py, color, 1.35, 1)
    end

    last_x = px
    last_y = py
  end
end

local function dotted_baseline(x, y, w)
  local step = 9
  local dot = 1.2
  for px = x, x + w, step do
    rect(px, y, dot, dot, "#d9d9d9", 0.9, dot)
  end
end

local function price_pill(value, x, y, color)
  rect(x, y, 62, 26, color, 1, 5)
  local label = text(value, x + 7, y + 5, 12, "#ffffff", "bold")
  label:set_width(50)
end

local function stock_row(stock, x, y)
  text(stock.symbol, x, y, 16, BLACK, "normal")
  text(stock.shares, x, y + 18, 12, MUTED, "bold")
  dotted_baseline(x + 122, y + 20, 86)
  sparkline(x + 116, y + 3, 92, 25, stock.color, stock.seed or 1, stock.trend or 0.1)
  price_pill(stock.price, x + 246, y + 2, stock.color)
  widget.line(x, y + 42, x + 310, y + 42, LINE, 1, 1)
end

local function symbol_from_instrument(url, cache)
  if cache[url] then
    return cache[url]
  end

  local ok, data = pcall(function()
    return http.get_json(url, headers())
  end)
  if ok and data and data.symbol then
    cache[url] = data.symbol
    return data.symbol
  end

  return "----"
end

local function fetch_portfolio()
  local data = http.get_json("https://api.robinhood.com/portfolios/", headers())
  local item = data.results and data.results[1] or data[1] or data
  local equity = tonumber(item.equity or item.market_value or 0) or 0
  local previous = tonumber(item.adjusted_equity_previous_close or item.equity_previous_close or 0) or 0
  local change = 0
  if previous > 0 then
    change = ((equity - previous) / previous) * 100
  end
  state.change_percent = change
  state.ok = true
  state.error = ""
end

local function fetch_stocks()
  local positions = http.get_json("https://api.robinhood.com/positions/?nonzero=true", headers())
  local instrument_cache = {}
  local rows = {}
  local symbols = {}

  if positions and positions.results then
    for _, position in ipairs(positions.results) do
      if #rows >= 5 then
        break
      end

      local quantity = tonumber(position.quantity or 0) or 0
      if quantity > 0 and position.instrument then
        local symbol = symbol_from_instrument(position.instrument, instrument_cache)
        rows[#rows + 1] = {
          symbol = symbol,
          shares = compact_shares(quantity),
          price = "$--",
          color = GREEN,
          seed = #rows + 0.7,
          trend = 0.14
        }
        symbols[#symbols + 1] = symbol
      end
    end
  end

  if #rows == 0 then
    symbols = split_symbols(symbol_setting)
    for i, symbol in ipairs(symbols) do
      if i > 5 then
        break
      end
      rows[#rows + 1] = {
        symbol = symbol,
        shares = "0 shares",
        price = "$--",
        color = GREEN,
        seed = i + 0.5,
        trend = 0.1
      }
    end
  end

  if #symbols > 0 then
    local quote_url = "https://api.robinhood.com/quotes/?symbols=" .. table.concat(symbols, ",")
    local quotes = http.get_json(quote_url, headers())
    local by_symbol = {}
    if quotes and quotes.results then
      for _, quote in ipairs(quotes.results) do
        if quote and quote.symbol then
          by_symbol[quote.symbol] = quote
        end
      end
    end

    for _, row in ipairs(rows) do
      local quote = by_symbol[row.symbol]
      if quote then
        local last = tonumber(quote.last_trade_price or quote.last_extended_hours_trade_price or quote.previous_close or 0) or 0
        local previous = tonumber(quote.previous_close or 0) or 0
        local change = last - previous
        row.price = dollars(last)
        row.color = change < 0 and RED or GREEN
        row.trend = change < 0 and -0.22 or 0.18
      end
    end
  end

  state.stocks = rows
  state.ok = true
  state.error = ""
end

local function fetch_data()
  if demo_mode then
    state.ok = true
    state.error = ""
    return
  end

  if bearer_token == "" then
    state.ok = false
    state.error = "Set bearer_token or enable demo mode"
    return
  end

  local ok, err = pcall(function()
    fetch_portfolio()
    if mode == "stocks" then
      fetch_stocks()
    end
  end)

  if not ok then
    state.ok = false
    state.error = tostring(err)
    widget.log("Robinhood fetch failed: " .. tostring(err))
  end
end

local function draw_error(message)
  widget.set_size(331, 159)
  rect(0, 0, 331, 159, CARD, 1, 18)
  robinhood_mark(294, 17, 0.75)
  text("Robinhood", 16, 16, 16, BLACK, "bold")
  local msg = text(message or "Unavailable", 16, 48, 12, RED, "bold")
  msg:set_width(292)
  text("Enable demo mode or add a token.", 16, 84, 12, MUTED, "normal")
end

local function draw_today()
  widget.set_size(331, 159)
  rect(0, 0, 331, 159, CARD, 1, 18)
  robinhood_mark(294, 17, 0.75)

  local is_up = (tonumber(state.change_percent or 0) or 0) >= 0
  local color = is_up and GREEN or RED
  local arrow = is_up and "/" or "\\"
  text(arrow, 16, 62, 29, color, "bold")
  text(percent(state.change_percent), 36, 62, 31, color, "normal")
  text("Today", 15, 100, 16, BLACK, "bold")
  text(state.updated or "10 sec ago", 15, 133, 12, MUTED, "bold")

  dotted_baseline(174, 92, 82)
  sparkline(178, 55, 116, 55, color, 1.7, is_up and 0.42 or -0.32)
end

local function draw_stocks()
  widget.set_size(331, 350)
  rect(0, 0, 331, 350, CARD, 1, 18)
  robinhood_mark(294, 17, 0.75)
  text("Your stocks", 16, 18, 16, BLACK, "bold")
  text(state.updated or "10 sec ago", 16, 39, 12, MUTED, "bold")

  local y = 74
  for i, stock in ipairs(state.stocks) do
    if i > 5 then
      break
    end
    stock_row(stock, 16, y)
    y = y + 53
  end
end

function on_load()
  mode = lower(widget.setting_choice("mode", "Widget mode", "today|stocks", "today"))
  bearer_token = widget.setting_text("bearer_token", "Robinhood bearer token", "")
  demo_mode = widget.setting_bool("demo_mode", "Demo mode", true)
  symbol_setting = widget.setting_text("symbols", "Fallback symbols", "POWA,ACOT,PTG,HKTP,SHB")
  widget.set_background("transparent", 1)
  fetch_data()
end

function on_update()
  mode = lower(widget.setting_choice("mode", "Widget mode", "today|stocks", mode))
  bearer_token = widget.setting_text("bearer_token", "Robinhood bearer token", bearer_token)
  demo_mode = widget.setting_bool("demo_mode", "Demo mode", demo_mode)
  symbol_setting = widget.setting_text("symbols", "Fallback symbols", symbol_setting)

  update_count = update_count + 1
  if update_count >= 60 then
    update_count = 0
    fetch_data()
  end

  widget.clear()
  if not state.ok then
    draw_error(state.error)
  elseif mode == "stocks" then
    draw_stocks()
  else
    draw_today()
  end
end
