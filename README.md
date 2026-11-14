# 划词朗读（Selection TTS）

Windows 10/11 的划词朗读原型。Phase 0 先用 Python + PySide6 验证取词、Edge TTS 音质/延迟和托盘交互，确认后再用 C# 重写正式版。

当前进度：**D1 + D2 完成**。已能通过全局快捷键读取当前选区，用 Edge TTS 合成并播放；自动弹窗和 UIA 取词在 D3/D4 接入。

## 环境要求

- Windows 10 22H2 / Windows 11
- [uv](https://docs.astral.sh/uv/) 0.13+
- 首次运行会自动使用 Python 3.12（`.python-version` 已固定）

## 安装与运行

```powershell
cd D:\桌面\TTS
uv sync
uv run selection-tts
```

启动后程序常驻系统托盘，不显示主窗口。

## 快捷键

| 快捷键 | 功能 |
|---|---|
| `Ctrl+Alt+R` | 朗读当前选区 |
| `Ctrl+Alt+Shift+P` | 开启/关闭自动弹窗（Phase 0 仅记录状态，D3 接入鼠标监听） |

> 原计划的 `Ctrl+Alt+P` 在这台机器上已被其他程序占用（Win32 错误 1409），因此默认改为 `Ctrl+Alt+Shift+P`。快捷键可在 `%APPDATA%\SelectionTTS\config.json` 中修改。

## 托盘菜单

- 朗读当前选区
- 自动弹窗开关
- 停止朗读
- 音色：晓晓 / 晓伊 / 云希 / 云健
- 语速：-20% / 0% / +20% / +50%
- 打开日志目录
- 退出

## 当前实现方式

1. 全局快捷键由 Win32 `RegisterHotKey` 注册，不依赖低层键盘钩子。
2. 触发后等待 250ms 让快捷键松开，再向当前前台窗口发送 `Ctrl+C`。
3. 读取剪贴板文本，并尽力恢复触发前的剪贴板内容。
4. 文本归一化、截断后交给 `edge-tts` 合成 MP3。
5. 用 `pygame` 播放，支持停止；托盘显示状态。

Phase 0 暂未接入：

- UI Automation 直接读选区（D4）
- 鼠标划选自动弹窗（D3）
- 全屏/游戏自动暂停（D6）

## 数据与隐私

- 配置：`%APPDATA%\SelectionTTS\config.json`
- 日志：`%LOCALAPPDATA%\SelectionTTS\logs\app.log`
- 指标：`%LOCALAPPDATA%\SelectionTTS\metrics.jsonl`
- 临时音频：`%LOCALAPPDATA%\SelectionTTS\cache\`

日志和指标**不记录选中的文本内容**，只记录字符数、耗时、音色、错误码等。朗读文本会发送到 Microsoft Edge TTS 在线接口，请自行评估隐私影响。

## 已知限制

- 管理员权限运行的程序：普通权限进程无法向它发送 `Ctrl+C`，当前直接视为不支持。
- 终端窗口：为避免 `Ctrl+C` 中断终端里的进程，当前会跳过（D4 用 UIA 尝试解决）。
- DRM 视频、游戏、部分 Java/Electron 应用可能无法复制选区。
- 剪贴板恢复是尽力而为：文本、HTML、RTF 等全局内存格式可恢复，GDI 句柄类格式（部分图片）暂不恢复。
- Edge TTS 是非官方接口，可能限流或变更；正式版会加 Azure 引擎和本地引擎兜底。
- Phase 0 从终端启动时带控制台窗口；正式版会改为无控制台托盘程序。

## 验证脚本

需要先启动 `uv run selection-tts`（除 D1 探针外）：

```powershell
uv run python scripts\probe_edge_tts.py    # D1：音色列表 + 首音/总合成耗时
uv run python scripts\probe_play.py        # D1：pygame 播放 probe_zh.mp3
uv run python scripts\probe_selection.py   # D2：Tk 选区 + Ctrl+C + 剪贴板恢复
uv run python scripts\probe_pipeline.py    # D2：合成 + 播放
uv run python scripts\probe_hotkey.py      # D2：真实全局热键端到端（会播放声音）
uv run python scripts\probe_toggle.py      # D2：自动弹窗开关热键
```

## 下一步

- D3：鼠标划选检测 + 无焦点浮窗
- D4：UIA TextPattern 优先取词 + 剪贴板兜底
- D5：通用场景测试矩阵
- D6：全屏/游戏自动暂停、进程排除
- D7：指标汇总与 go/no-go

详细记录见 `docs/d2-report.md`。
