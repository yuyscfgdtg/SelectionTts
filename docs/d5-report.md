# Phase 0 / D5 验证报告：通用场景测试矩阵

日期：2026-11-14  
机器：Windows 11 Build 26300，2560×1440，缩放 100%，默认终端为 Windows Terminal  
目录：`D:\桌面\TTS`  
脚本：`uv run python scripts\probe_matrix.py [场景…]`

## 目标

验证同一套取词逻辑（UIA 优先、剪贴板兜底）在常见应用里的命中率、取词方式和耗时，找出必须跳过的场景和需要修的缺陷。

矩阵只取词、不合成、不播放；每个场景都会把目标窗口置顶、点击标题栏激活、选中内容，再调用产品使用的 `read_selection()`。

## 测试方法

| 步骤 | 说明 |
|---|---|
| 启动目标 | 用脚本/命令行打开真实应用，不注入进程 |
| 激活 | `SetWindowPos(HWND_TOPMOST)` + 点击标题栏；`SetForegroundWindow` 在后台进程里会被系统拒绝 |
| 选中 | 普通应用 `Ctrl+A`；Windows Terminal `Ctrl+Shift+A`；conhost 用鼠标拖选 |
| 取词 | `read_selection(prefer_uia=True, wait_release_ms=0, restore=True)` |
| 判定 | 取到的文本必须包含场景标记（如 `MATRIX-WORD`） |

## 结果

| 场景 | 窗口类 | 取词方式 | 字符 | 选中→取词 | 结果 |
|---|---|---|---|---|---|
| 记事本 | `Notepad` | UIA `TextPattern` | 25 | 40ms | PASS |
| Chrome | `Chrome_WidgetWin_1` | UIA `TextPattern` | 63 | 3ms | PASS |
| Edge | `Chrome_WidgetWin_1` | UIA `TextPattern` | 61 | 3ms | PASS |
| 资源管理器 | `CabinetWClass` | 主动跳过 | 0 | 0ms | SKIP |
| Windows Terminal | `CASCADIA_HOSTING_WINDOW_CLASS` | UIA `TextPattern` | 134 | 5ms | PASS |
| Word | `OpusApp` | UIA `TextPattern` | 44 | 4ms | PASS |
| Edge PDF 阅读器 | `Chrome_WidgetWin_1` | UIA `TextPattern` | 28 | 4ms | PASS |
| Excel | `XLMAIN` | 剪贴板 | 28 | 0.06–1.5s | PASS |

4 个应用（Chrome、Edge、Word、PDF）在同一轮里 UIA 命中，说明 Chromium/Office 在收到无障碍查询后会暴露 `TextPattern`；首次查询可能稍慢，失败时仍会自动回退剪贴板。

## 场景说明

### 记事本 / Word / 浏览器 / PDF

UIA 直接返回焦点控件的选区文本，不碰剪贴板，耗时 3–40ms。

- 浏览器和 PDF 的选区在 Chromium 的文档节点上；`uia.py` 会从焦点控件向上找 `TextPattern`。
- PDF 文本层由阅读器重新排版，取到的文本会**丢失部分空格和连字符**（例：`MATRIXPDF这是PDF...`），朗读时影响很小，但复制/展示要注意。
- 记事本单实例：窗口 PID 与启动 PID 不同，脚本按标题查找窗口。

### Windows Terminal

- `TermControl` 实现 `TextPattern`，选区可直读，5ms。
- 终端全选是 `Ctrl+Shift+A`（`Ctrl+A` 在 shell 里是行首）。
- 取到的是终端缓冲区文本，含行尾补空格和空行，交给 `normalize_text()` 合并多余空格后朗读。
- 传统 conhost（`ConsoleWindowClass`）未能在本机复现（默认终端已被 Windows Terminal 接管），沿用「跳过剪贴板兜底」策略，避免 `Ctrl+C` 打断前台进程。

### 资源管理器（主动跳过）

资源管理器没有「文本选区」：`Ctrl+A` + `Ctrl+C` 复制的是文件列表（`CF_HDROP`），不是文本。

之前的行为是：发 `Ctrl+C` → 剪贴板序列号变化 → 读不到文本 → 白等 1.2s，还把用户剪贴板换成了文件。现在 `CabinetWClass` / `ExploreWClass` / `Progman` / `WorkerW` 直接跳过剪贴板兜底，0ms 返回空文本，不触碰剪贴板。

### Excel（剪贴板 + 延迟渲染）

- 单元格网格不提供 `TextPattern`，走 `Ctrl+C` 兜底。
- Excel 使用延迟渲染：序列号已经变化时，第一次读剪贴板通常还是空的。
- 修复：序列号变化后轮询读取最多 600ms。修复前读不到文本且耗时 1.3–2.1s；修复后通常 60–100ms 拿到文本，最慢一轮 1.5s（首次启动 Excel、剪贴板被占用时）。
- 修复后剪贴板能正常恢复（实测 `clipboard_restored=True`）。Excel 启动瞬间可能短暂占用剪贴板，导致本轮快照失败、无法恢复原内容，属于已知限制。

## 本轮修复

| 问题 | 修复 |
|---|---|
| 资源管理器双击文件时白等 1.2s 并覆盖剪贴板 | `selection.py` 新增文件管理器类跳过 |
| Excel 延迟渲染导致首次读取为空 | 序列号变化后轮询读取，最多 600ms |
| 测试脚本无法把后台窗口切到前台 | 置顶 + 点击标题栏激活（仅测试脚本，产品不依赖） |

## 未覆盖 / 后续反馈

不再逐个应用铺测试；矩阵覆盖了原生控件、Chromium、Office、PDF、终端五类代表性实现，其余应用如果出现「划词没反应/读错内容」，由使用者直接反馈具体应用，再针对性排查。

| 应用 | 说明 |
|---|---|
| VS Code / 微信等未安装应用 | 未测，遇到问题单独反馈 |
| 游戏 / 全屏应用 | 属于 D6 范围，做全屏检测和进程排除 |
| 管理员权限窗口 | 普通权限进程无法 UIA/发键；需要时以管理员身份运行本程序 |
| 传统 conhost | 本机默认终端是 Windows Terminal，未复现；策略仍是跳过剪贴板兜底 |

## D5 判定

D5 通过：8 个场景中 7 个可用（含 1 个按设计跳过），无阻塞性缺陷。浏览器、Office、PDF、终端都能在 50ms 内完成 UIA 取词；Excel 走剪贴板兜底并已修复延迟渲染问题。

下一步 D6：全屏/游戏自动暂停、进程排除。
