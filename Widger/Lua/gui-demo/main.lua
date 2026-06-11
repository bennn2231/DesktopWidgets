local clicks = 0
local playing = false

function on_load()
  widget.set_size(240, 120)
  widget.set_background("transparent", 1)
end

function on_update()
  local ui = widget.ui
  ui.begin()
  ui.panel(0, 0, 240, 120, "#0d0d0d", 0.94, 14, "156,163,175", 1)
  ui.label("Immediate UI", 16, 13, 16, "#ffffff", "bold")
  ui.label("clicks " .. clicks, 16, 40, 13, "#d4d4d4")

  if ui.button("Count", 16, 76, 72, 28, "count") then
    clicks = clicks + 1
  end

  if ui.icon_button(playing and "pause" or "play", 104, 76, 36, 28, "play-toggle") then
    playing = not playing
  end

  if ui.icon_button("refresh", 154, 76, 36, 28, "reset") then
    clicks = 0
  end

  ui.end_frame()
end
