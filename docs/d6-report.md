# Phase 0 / D6 验证报告：全屏暂停与进程排除

日期：2026-11-14  
机器：Windows 11 Build 26300，2560×1440，缩放 100%，单显示器  
目录：`D:\桌面\TTS`

## 目标

1. 全屏应用（游戏、全屏视频、演示）里不弹朗读按钮，避免干扰；
2. 允许把指定程序加入排除列表，在这些程序里完全不取词；
3. 不影响普通窗口下的热键和弹窗。

## 实现

| 位置 | 变化 |
|---|---|
| `fullscreen.py` | 新增 `is_foreground_fullscreen()`：前台窗口矩形与所在显示器矩形一致即视为全屏；保留 `SHQueryUserNotificationState` 检查；`is_fullscreen_now()` 合并两者 |
| `foreground.py` | 新增 `get_process_name(pid)` / `get_foreground_process_name()`，用 `QueryFullProcessImageNameW` 取可执行文件名 |
| `config.py` | 新增 `pause_in_fullscreen`（默认 `true`）、`excluded_processes`（默认空，自动补 `.exe`、去重） |
| `selection_monitor.py` | 手势通过后先检查暂停原因：排除列表 → 或全屏 → 直接返回；状态变化只记一次日志 |
| `app.py` | 热键朗读前检查排除列表；托盘新增「全屏/游戏时暂停自动弹窗」开关 |

暂停策略：

| 场景 | 自动弹窗 | 热键 `Ctrl+Alt+R` |
|---|---|---|
| 普通窗口 | 可用 | 可用 |
| 全屏/游戏 | 暂停 | 可用（用户主动触发） |
| 排除列表中的程序 | 暂停 | 暂停，托盘提示「已跳过」 |

全屏检测同时用两种信号：

- `SHQueryUserNotificationState`：D3D 独占全屏、演示模式、忙碌状态；
- 前台窗口矩形 == 显示器矩形（容差 2px）：无边框全屏游戏和视频播放器。

多显示器下用 `MonitorFromWindow` 取窗口所在显示器，副屏全屏也能识别；桌面、任务栏等 shell 窗口被排除。

## 验证

### 1. 检测逻辑（`scripts\probe_guard.py`）

| 检查 | 结果 |
|---|---|
| 普通窗口不判为全屏 | PASS |
| Tk 全屏窗口判为全屏 | PASS |
| 合并检查 `is_fullscreen_now()` | PASS |
| 退出全屏后恢复 | PASS |
| 能取到进程名（`python.exe`） | PASS |
| 排除列表命中 | PASS |
| 排除列表不误伤其他进程 | PASS |

### 2. 全屏暂停端到端（`scripts\probe_pause.py`）

托盘程序运行中，打开一个置顶的 Tk 全屏窗口并拖选文字：

- 3.5s 内没有出现 `SelectionTTS Popup`；
- metrics 没有新的 `popup` 事件；
- 应用日志：`自动弹窗已暂停（全屏）class=TkTopLevel title='SelectionTTS pause probe'`。

### 3. 排除列表端到端

临时把 `excluded_processes` 设为 `["python.exe"]` 并重启托盘程序，用 `probe_popup.py` 在 Tk 窗口拖选：

- 弹窗没有出现，也没有 `popup` 事件；
- 应用日志：`自动弹窗已暂停（排除列表）class=TkTopLevel title='SelectionTTS - popup probe'`；
- 关闭测试窗口后日志出现 `自动弹窗已恢复`。

测试后已恢复原配置（`excluded_processes` 为空）。

### 4. 回归

| 检查 | 结果 |
|---|---|
| `probe_popup.py`（拖选→弹窗→点击朗读→焦点不丢） | PASS |
| `probe_hotkey_uia.py`（热键→UIA→朗读） | PASS，`method=uia`，`selection_ms=7` |

期间还修了测试脚本的一个竞态：鼠标瞬移后立即点击会被钩子误判为拖选（`distance=521`），导致测试里弹出真实弹窗；在设置坐标和点击之间加 150ms 等待后恢复稳定。真实鼠标操作不存在这种瞬移。

## 已知限制

- 独占全屏的个别游戏可能既不报告 D3D 全屏、窗口矩形也不等于显示器矩形；这类程序请加入 `excluded_processes`。
- 全屏暂停只针对自动弹窗；热键仍然可用，这是刻意保留的逃生口。
- 进程名按可执行文件名匹配，不区分路径，同名的不同程序会一起排除。
- 托盘开关只控制「全屏暂停」；排除列表通过 `%APPDATA%\SelectionTTS\config.json` 配置，修改后需重启。

## D6 判定

D6 通过：全屏与排除列表两条路径都已在运行中的程序里验证，普通窗口的弹窗与热键没有回归。

下一步 D7：汇总指标、给出 go/no-go。
