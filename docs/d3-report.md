# Phase 0 / D3 验证报告：划选弹窗

日期：2026-11-14  
机器：Windows 11 Build 26300，2560×1440，缩放 100%  
目录：`D:\桌面\TTS`

## 目标

划选文本后自动在光标旁出现「朗读」按钮；点击后直接朗读已取到的文本。弹窗不得抢走源窗口焦点，也不应在点击弹窗时再次触发取词。

## 实现

| 组件 | 作用 |
|---|---|
| `selection_monitor.py` | `pynput` 低层鼠标钩子；检测拖选（≥6px 且 ≥80ms）、双击选词、Shift 点选 |
| `fullscreen.py` | `SHQueryUserNotificationState` 判断全屏游戏/演示/忙碌状态，命中则跳过 |
| `popup.py` | Qt 无边框浮窗；`WS_EX_NOACTIVATE` + `WindowDoesNotAcceptFocus` |
| `reader.py` | 新增 `read_text()`，点击弹窗后直接用已取文本合成，不再复制 |
| `app.py` | 连接鼠标监听、弹窗与托盘开关 |

关键细节：

1. 鼠标松开后默认延迟 120ms 取词，避免和目标应用的选区渲染竞争。
2. 弹窗矩形用 Win32 `GetWindowRect` 以**物理像素**上报，和鼠标钩子坐标系一致；定位时按 `devicePixelRatio` 换算成 Qt 逻辑像素。
3. 点击落在弹窗矩形内时，监听器直接忽略，不会把点击当成新的划选手势。
4. 取词前检查前台窗口：本程序、桌面/任务栏、终端类窗口一律跳过。
5. 弹窗默认 4 秒无操作自动隐藏；按 Esc 不处理（Phase 0 未接键盘钩子）。
6. 关闭自动弹窗只停用鼠标监听，`Ctrl+Alt+R` 始终可用。

## 验证结果

### 划选 → 弹窗 → 点击朗读

命令：`uv run python scripts\probe_popup.py`（需托盘程序运行且自动弹窗开启）

三次运行结果：

| 指标 | 结果 |
|---|---|
| 弹窗出现 | 通过，拖选后约 2.4s 内完成取词并显示（含 120ms 延迟与合成准备） |
| `popup` 指标事件 | 通过，`chars` 26–37，`selection_ms` 67–107ms，`clipboard_restored=true` |
| 焦点是否被抢 | **否**，前台窗口始终是源 Tk 窗口 |
| 点击「朗读」 | 通过，记录 `read` 事件 `method="popup"` |
| 点击后合成 | 1295–1931ms（一次成功），随后正常播放 |
| 重复取词 | 未发生；点击弹窗只产生一条 `read`，无额外 `popup` |

示例指标：

```json
{"event":"popup","ok":true,"chars":36,"selection_ms":86,"method":"clipboard","clipboard_restored":true}
{"event":"read","ok":true,"method":"popup","chars":36,"synthesis_ms":1931,"attempts":1,"clipboard_restored":null}
```

### 超时自动隐藏

命令：`uv run python scripts\probe_popup_hide.py`（独立运行，不依赖托盘状态）

- 配置 `popup_timeout_ms=1200`，2.0s 后 `IsWindowVisible` 为 `False`。
- 结果：**PASS**。

### 开关联动

`Ctrl+Alt+Shift+P` 关闭后鼠标监听停止，托盘/日志同步；再次开启后恢复。关闭期间快捷朗读仍可用。

## 过程中发现并修复的问题

1. **坐标系统不一致**
   - 鼠标钩子给物理像素，Qt 窗口定位用逻辑像素；高 DPI 下弹窗位置和点击命中都会偏。
   - 修复：定位时除以 `devicePixelRatio`；上报给监听器的矩形改用 `GetWindowRect` 物理像素。

2. **点击弹窗可能被当成新手势**
   - 若命中矩形不准，点击释放会被当作一次普通点击（通常不触发），但在双击间隔内连续点击可能误触发。
   - 修复：按下时若在弹窗矩形内就清空 `_press_pos`，释放时直接返回。

3. **弹窗隐藏时机与取词线程竞争**
   - 取词线程带 `generation` 校验；用户再次按下鼠标或关闭自动弹窗时，旧线程结果会被丢弃。

## 已知限制

- 依赖全局鼠标钩子；远程桌面、部分安全软件/反作弊可能拦截。
- 终端（Windows Terminal / conhost）与桌面、任务栏被跳过；终端取词留待 D4 用 UIA 解决。
- 全屏游戏/演示模式只做跳过，未实现“暂停后恢复”的显式状态。
- 弹窗跟随鼠标松开点，未做多显示器边界之外的额外避让（已按可用区域夹紧）。
- 一次取词仍会短暂占用剪贴板（UIA 在 D4 接入后优先走 UIA）。

## D3 判定

D3 通过。下一步进入 D4：UIA `TextPattern` 优先取词，失败再回退 `Ctrl+C`。
