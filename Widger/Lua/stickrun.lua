local w = 360
local h = 180
local tick = 0
local score = 0
local alive = true
local runner_y = 70
local velocity_y = 0
local ground_y = 70
local hurdle_x = 340

local title
local score_text
local hint
local runner = {}
local hurdle = {}
local cloud = {}

local function rect(x, y, width, height, color, opacity, radius)
  return widget.rect(x, y, width, height, color, opacity, radius)
end

local function set_rect(part, x, y)
  part:set_position(x, y)
end

local function build_runner()
  runner.head = rect(52, 70, 18, 18, "#f8f6ef", 1, 9)
  runner.body = rect(59, 89, 6, 30, "#f8f6ef", 1, 3)
  runner.arm_l = rect(42, 96, 18, 5, "#f8f6ef", 1, 3)
  runner.arm_r = rect(64, 96, 19, 5, "#f8f6ef", 1, 3)
  runner.leg_l = rect(47, 119, 18, 5, "#f8f6ef", 1, 3)
  runner.leg_r = rect(62, 119, 18, 5, "#f8f6ef", 1, 3)
end

local function move_runner(y, stride)
  set_rect(runner.head, 52, y)
  set_rect(runner.body, 59, y + 19)
  set_rect(runner.arm_l, 42 + stride, y + 26)
  set_rect(runner.arm_r, 64 - stride, y + 26)
  set_rect(runner.leg_l, 47 - stride, y + 49)
  set_rect(runner.leg_r, 62 + stride, y + 49)
end

local function build_hurdle()
  hurdle.top = rect(295, 122, 32, 5, "#f4c95d", 1, 2)
  hurdle.left = rect(299, 127, 5, 22, "#f4c95d", 1, 2)
  hurdle.right = rect(319, 127, 5, 22, "#f4c95d", 1, 2)
end

local function move_hurdle(x)
  set_rect(hurdle.top, x, 122)
  set_rect(hurdle.left, x + 4, 127)
  set_rect(hurdle.right, x + 24, 127)
end

local function build_cloud()
  cloud.one = rect(238, 34, 24, 9, "#c7e7ff", 0.45, 5)
  cloud.two = rect(255, 28, 30, 12, "#c7e7ff", 0.45, 6)
  cloud.three = rect(281, 35, 24, 8, "#c7e7ff", 0.45, 4)
end

local function move_cloud(x)
  set_rect(cloud.one, x, 34)
  set_rect(cloud.two, x + 17, 28)
  set_rect(cloud.three, x + 43, 35)
end

local function reset_game()
  tick = 0
  score = 0
  alive = true
  runner_y = ground_y
  velocity_y = 0
  hurdle_x = 340
  hint:set_text("left click to jump")
  score_text:set_text("score 000")
  move_runner(runner_y, 0)
  move_hurdle(hurdle_x)
end

function on_load()
  widget.set_size(w, h)
  widget.set_background("transparent", 1)
  rect(0, 0, w, h, "#111827", 0.82, 14)
  rect(0, 150, w, 6, "#7dd3fc", 0.95, 3)
  rect(0, 156, w, 24, "#0f172a", 0.9, 0)
  title = widget.text("Stick Runner", 14, 12, 16, "#ffffff", "bold", "Segoe UI")
  score_text = widget.text("score 000", 270, 13, 13, "#bae6fd", "normal", "Consolas")
  hint = widget.text("left click to jump", 14, 160, 11, "#94a3b8", "normal", "Segoe UI")
  build_cloud()
  build_runner()
  build_hurdle()
  reset_game()
end

function on_click(x, y)
  if not alive then
    reset_game()
    return
  end

  if runner_y >= ground_y then
    velocity_y = -18
  end
end

function on_update()
  tick = tick + 1

  local cloud_x = 360 - ((tick * 3) % 460)
  move_cloud(cloud_x)

  if not alive then
    return
  end

  velocity_y = velocity_y + 2.8
  runner_y = runner_y + velocity_y
  if runner_y > ground_y then
    runner_y = ground_y
    velocity_y = 0
  end

  local stride = (tick % 4) * 4
  if stride > 8 then
    stride = 16 - stride
  end
  if runner_y < ground_y then
    stride = 0
  end
  move_runner(runner_y, stride)

  hurdle_x = hurdle_x - 11
  if hurdle_x < -40 then
    hurdle_x = 360
    score = score + 1
    score_text:set_text("score " .. string.format("%03d", score))
  end
  move_hurdle(hurdle_x)

  local hit_x = hurdle_x < 80 and hurdle_x > 34
  local hit_y = runner_y > 48
  if hit_x and hit_y then
    alive = false
    hint:set_text("crashed - left click to restart")
  end
end
