# Phase 0 / D4 验证报告：UIA 优先取词

日期：2026-11-14  
机器：Windows 11 Build 26300，2560×1440，缩放 100%  
目录：`D:\桌面\TTS`

## 目标

取词优先使用 UI Automation 的 `TextPattern`，不占用剪贴板；不支持 `TextPattern` 的应用自动回退 `Ctrl+C`。

## 实现

| 组件 | 变化 |
|---|---|
| `uia.py`（新增） | 从焦点控件向上最多 6 层查找 `TextPattern`，读取 `GetSelection()` |
| `selection.py` | 新增 `read_selection()`：先 UIA，按状态决定是否回退剪贴板 |
| `reader.py` | 热键朗读改用 `read_selection()` |
| `selection_monitor.py` | 划选弹窗改用 `read_selection()` |
| `config.py` | 新增 `use_uia`（默认 `true`） |

关键细节：

1. UIA 调用放在一次性 daemon 线程里，最多等待 1.0s；目标应用卡死时不会拖住朗读流程。
2. 焦点控件属于本进程，或 `IsPassword=true` 时，**不回退剪贴板**，避免朗读自己的界面或密码框。
3. 选中范围可能有多段，按顺序拼接；单次读取上限 20000 字，之后仍按 `max_chars` 截断。
4. UIA 失败（无 `TextPattern`、无选区、超时、异常）才走原有 `Ctrl+C` 兜底。
5. `TextPattern` 在祖先控件上时也会向上查找，兼容编辑框嵌在面板里的应用。

## 验证结果

### UIA 直接取词

命令：`uv run python scripts\probe_uia.py`

目标：`scripts\uia_target.ps1` 启动的 WPF `TextBox`（实现 `ITextProvider`）。

| 指标 | 结果 |
|---|---|
| method | `uia` |
| 文本 | 45 字，与源文本完全一致 |
| 耗时 | **44ms** |
| 剪贴板 | 未改变 |

### 热键端到端（UIA）

命令：`uv run python scripts\probe_hotkey_uia.py`（托盘程序需运行）

metrics：

```json
{"event":"read","ok":true,"method":"uia","chars":32,"selection_ms":54,"synthesis_ms":1337,"attempts":1,"clipboard_restored":null}
```

- 热键触发后 54ms 完成 UIA 取词，未写剪贴板。
- 合成 1337ms，正常播放。

### 剪贴板兜底（无 TextPattern）

命令：`uv run python scripts\probe_selection.py`

目标：独立进程中的 Tk 窗口（Tk 不提供 `TextPattern`）。

| 指标 | 结果 |
|---|---|
| method | `clipboard` |
| 文本 | 与源文本一致 |
| UIA 尝试开销 | 约 46ms（wall 132ms，其中复制 85ms） |
| 剪贴板恢复 | `true` |

### 划选弹窗回归

命令：`uv run python scripts\probe_popup.py`

Tk 目标按预期回退剪贴板：`popup` 事件 `selection_ms=86`，点击朗读 `method="popup"`，焦点未被抢走，**PASS**。

## 过程中的发现

1. **标准 Win32 `EDIT` 不提供 UIA `TextPattern`**
   - 用原生 EDIT 做目标时 `GetPattern(TextPattern)` 为空，只能走剪贴板。
   - 因此改用 WPF `TextBox`（`TextBoxAutomationPeer` 实现 `ITextProvider`）作为确定性 UIA 目标。
2. **Windows Terminal 的 `TermControl` 提供 `TextPattern`**
   - 诊断时确认其 `GetPattern(TextPattern)` 可用；终端选区有机会直接通过 UIA 读取。
   - 需要目标终端实际有选区；无选区时返回空串并回退。
3. **Tk / Qt 不实现 `TextPattern`**
   - 这类应用始终走剪贴板兜底，符合预期。

## 已知限制

- Chrome/Edge 默认不向 UIA 暴露正文选区（需开启辅助功能），实际仍走剪贴板。
- UIA 目标进程卡死时线程只能被放弃，进程里可能残留一个等待中的 UIA 线程（已限制 1s，不阻塞朗读）。
- 密码框已显式跳过，但部分自绘控件可能错误报告 `IsPassword`，需要 D5 实测。
- 管理员权限窗口：普通权限进程既不能 UIA 读取，也不能发送 `Ctrl+C`。
- UIA 读取的是“焦点控件”的选区；某些应用把焦点放在工具栏时可能取不到，需 D5 记录。

## D4 判定

D4 通过。下一步进入 D5：在浏览器、Office/记事本、PDF、VS Code、微信、终端等场景跑通用测试矩阵，记录 UIA/剪贴板命中率与延迟。
