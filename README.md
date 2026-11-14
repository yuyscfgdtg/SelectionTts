# 划词朗读（Selection TTS）

Windows 10/11 的划词朗读原型。Phase 0 先用 Python + PySide6 验证取词、Edge TTS 音质/延迟和托盘交互，确认后再用 C# 重写正式版。

当前进度：**D1–D4 完成**。取词优先走 UI Automation，失败再回退 Ctrl+C；全局快捷键和划词弹窗都已接入。下一步是 D5 通用场景测试矩阵。

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
| `Ctrl+Alt+Shift+P` | 开启/关闭自动弹窗 |

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
2. 取词顺序：先用 UI Automation 读取焦点控件的 `TextPattern` 选区（不碰剪贴板），失败再回退 `Ctrl+C`。
3. 快捷朗读：等待 250ms 让快捷键松开，再执行取词。
4. 鼠标划选：低层鼠标钩子检测拖选、双击选词和 Shift 点选；松开后延迟 120ms 取词。
5. 剪贴板兜底：读取文本后尽力恢复触发前的内容。
6. 文本归一化、截断后交给 `edge-tts` 合成 MP3。
7. 用 `pygame` 播放，支持停止；托盘显示状态。
8. 划选后弹出无焦点浮窗（`WS_EX_NOACTIVATE`），4 秒无操作自动隐藏；点击「朗读」直接播放已取到的文本，不会再次复制。

Phase 0 暂未接入：

- 全屏/游戏自动暂停（D6）

## 数据与隐私

- 配置：`%APPDATA%\SelectionTTS\config.json`
- 日志：`%LOCALAPPDATA%\SelectionTTS\logs\app.log`
- 指标：`%LOCALAPPDATA%\SelectionTTS\metrics.jsonl`
- 临时音频：`%LOCALAPPDATA%\SelectionTTS\cache\`

日志和指标**不记录选中的文本内容**，只记录字符数、耗时、音色、错误码等。朗读文本会发送到 Microsoft Edge TTS 在线接口，请自行评估隐私影响。

## 已知限制

- 管理员权限运行的程序：普通权限进程无法向它发送 `Ctrl+C`；UIA 通常也读不到，视为不支持。
- 终端窗口：剪贴板兜底会被跳过；如果终端提供 UIA `TextPattern`（如 Windows Terminal），仍可通过 UIA 读取。
- 浏览器：Chrome/Edge 默认不向 UIA 暴露正文选区，会走剪贴板兜底。
- Qt/Tk、游戏和部分 Electron 应用不提供 `TextPattern`，同样走剪贴板兜底。
- DRM 视频、游戏、部分 Java/Electron 应用可能无法复制选区。
- 划选检测依赖全局鼠标钩子：某些全局鼠标工具、远程桌面或反作弊程序可能拦截钩子。
- 双击选词、拖选、Shift 点选会弹窗；普通单击不会。终端窗口和桌面/任务栏会被跳过。
- 弹窗显示期间不会抢焦点，但需要目标窗口本身支持 `Ctrl+C` 复制选区。
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
uv run python scripts\probe_popup.py       # D3：拖选→弹窗→点击朗读→焦点不丢失
uv run python scripts\probe_popup_hide.py  # D3：弹窗超时自动隐藏（无需托盘程序）
uv run python scripts\probe_uia.py         # D4：UIA 直接取词（WPF 目标，不碰剪贴板）
uv run python scripts\probe_hotkey_uia.py  # D4：热键→UIA→朗读端到端
```

## 下一步

- D5：通用场景测试矩阵
- D6：全屏/游戏自动暂停、进程排除
- D7：指标汇总与 go/no-go

详细记录见 `docs/d2-report.md`、`docs/d3-report.md`、`docs/d4-report.md`。
