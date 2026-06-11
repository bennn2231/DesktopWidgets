local bg
local time_text
local date_text

function on_load()
  widget.set_background("transparent", 1)
  bg = widget.rect(0, 0, 260, 92, "#101418", 0.55, 10)
  time_text = widget.text("", 18, 8, 36, "#ffffff", "bold")
  date_text = widget.text("", 20, 55, 15, "#c7d0d9", "normal")
end

function on_update()
  time_text:set_text(widget.now("h:mm tt"))
  date_text:set_text(widget.now("dddd, MMMM dd"))
end
