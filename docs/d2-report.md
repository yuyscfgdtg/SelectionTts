# Phase 0 / D1 + D2 验证报告

日期：2026-11-14  
机器：Windows 11 Build 26300  
目录：`D:\桌面\TTS`

## 环境

| 组件 | 版本 |
|---|---|
| Python | 3.12.16（uv 管理） |
| edge-tts | 7.3.1 |
| PySide6 | 6.12.0 |
| pygame | 2.6.1 |
| pywin32 | 312 |
| pynput | 1.8.2 |

## D1：Edge TTS 连通性与音质

命令：`uv run python scripts/probe_edge_tts.py`

结果：

- 可用 `zh-CN` 音色 8 个：晓晓、晓伊、云希、云健、云夏、云扬、晓北（东北）、晓妮（陕西）。
- 测试文本 36 字：
  - 首个音频块：**0.902s**
  - 总合成：**1.421s**
  - 音频大小：47,520 字节
- 播放入口：`pygame.mixer` 可正常播放 Edge TTS 生成的 MP3。

结论：当前网络下 Edge TTS 可用，首音延迟约 0.9s，满足 Phase 0 目标（≤1.5s）。

## D2：取词、热键、播放链路

### 选区复制 + 剪贴板恢复

命令：`uv run python scripts\probe_selection.py`

结果：

- Tk 选区文本正确捕获（18 字中文）。
- `selection_ms`：**86ms**（不含热键松开等待）。
- 剪贴板恢复：**True**，触发前的 `ORIGINAL_CLIPBOARD` 被完整恢复。
- 注意：测试脚本必须让 Tk 事件循环保持运行，否则 Tk 不会处理合成的 `Ctrl+C`；这是测试环境问题，不是产品缺陷。

### 合成 + 播放

命令：`uv run python scripts\probe_pipeline.py`

结果：

- 合成：**1.65s**，一次成功。
- 播放：成功，总耗时约 6.4s（含音频时长）。

### 全局热键端到端

命令：`uv run python scripts\probe_hotkey.py`（应用需先运行）

结果（metrics.jsonl）：

```json
{"event":"read","ok":true,"method":"clipboard","chars":32,"selection_ms":318,"synthesis_ms":1941,"attempts":1,"clipboard_restored":true,"voice":"zh-CN-XiaoxiaoNeural","rate":"+0%"}
```

- 热键 `Ctrl+Alt+R` 触发成功。
- 取词 318ms（含 250ms 等待快捷键松开）。
- 合成 1941ms，播放完成，剪贴板恢复成功。
- 用户确认**能听到声音**。

### 自动弹窗开关热键

命令：`uv run python scripts\probe_toggle.py`

结果：`auto_popup` 依次变为 `False`、`True`，**PASS**。

## 过程中发现并修复的问题

1. **`Ctrl+Alt+P` 被占用**
   - Win32 `RegisterHotKey` 返回 1409（ERROR_HOTKEY_ALREADY_REGISTERED）。
   - 默认改为 `Ctrl+Alt+Shift+P`，两个快捷键均注册成功。

2. **`pynput.GlobalHotKeys` 对合成按键不触发**
   - 低层键盘钩子能看到按键，但 `GlobalHotKeys` 状态机没有触发。
   - 改用 Win32 `RegisterHotKey` + Qt `nativeEvent` 处理 `WM_HOTKEY`。
   - 好处：不依赖低层键盘钩子，减少与反作弊/安全软件冲突的概率，合成按键测试也能通过。

3. **纯 `Ctrl+C` 兜底可能打到终端**
   - 如果前台不是选中文本的窗口而是终端，`Ctrl+C` 可能中断终端里的进程，甚至杀掉应用自身。
   - 加入前台窗口检查：前台属于本进程或终端类窗口时跳过。
   - 同时调用 `SetConsoleCtrlHandler(None, TRUE)`，避免应用被误发的 `Ctrl+C` 终止。

4. **Tk 测试窗口不处理合成 `Ctrl+C`**
   - `time.sleep()` 阻塞了 Tk 事件循环。
   - 测试改为后台线程执行取词、主线程跑 `mainloop()`。

## 仍未解决 / 后续

- UIA 直接取词尚未接入；当前所有取词都走 `Ctrl+C`，会短暂占用剪贴板。
- 剪贴板快照只恢复全局内存格式；图片 GDI 句柄类格式暂不恢复。
- 自动弹窗开关目前只保存状态，D3 才接入鼠标监听。
- 终端窗口当前直接跳过；D4 用 UIA 尝试覆盖 Windows Terminal。
- Edge TTS 为非官方接口，正式版需要 Azure/本地引擎兜底。
- Phase 0 仍有控制台窗口；正式版改为无控制台托盘程序。

## D2 判定

D2 通过。下一步进入 D3：鼠标划选检测 + 无焦点浮窗。
