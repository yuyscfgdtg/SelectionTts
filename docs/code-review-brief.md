# 代码审查说明 · 剪贴板朗读（SelectionTts）

> 用途：给外部审查者（大语言模型或人工）提供一份**功能全貌 + 实现方式 + 关键不变量 + 已知取舍**的说明，便于针对性审查代码质量、并发正确性、资源管理、协议实现与安全边界。
>
> 代码仓库：`https://github.com/yuyscfgdtg/SelectionTts`（MIT）
> 当前版本：**1.0.2**（稳定性修复版，未发布；本文以 v1.0.1 基线写成，修复项见各章节）
> 代码规模：30 个源文件（C# + XAML + csproj + iss），**5,204 行**，约 206 KB
> 目标平台：Windows 10/11 x64，`.NET 10 (net10.0-windows)`，WPF + Windows Forms 混合

---

## 1. 产品定位

一个 **Windows 托盘常驻的剪贴板朗读工具**：

- 用户复制任意文本 → 自动送入 TTS 合成 → 边合成边播放
- 一个**置顶但不抢焦点**的「朗读板」浮窗显示正文、读到第几行、合成进度
- 点朗读板任意一行 → 从那里继续读（句首对齐）
- 全程可用托盘菜单与全局热键操作，无主窗口

设计上刻意保持「小、可解释、无侵入」：不注入进程、不读屏幕、不抓选区，只消费剪贴板。

---

## 2. 技术栈与交付形态

| 项 | 内容 |
|---|---|
| 语言 / 框架 | C# 13，.NET 10，WPF（UI）+ WinForms（`NotifyIcon`、`Clipboard`、`NativeWindow`） |
| 音频 | NAudio 2.2.1（`WaveOutEvent` + `BufferedWaveProvider` + `Mp3FileReader`） |
| JSON | `System.Text.Json`（camelCase，缩进，大小写不敏感读取） |
| TTS 主引擎 | **Edge 朗读接口**（`wss://speech.platform.bing.com/.../edge/v1`，非官方，与 edge-tts 同协议），自实现 WebSocket 客户端 + DRM 令牌 |
| TTS 备用引擎 | **Azure Speech REST**（`https://{region}.tts.speech.microsoft.com/cognitiveservices/v1`） |
| 敏感数据 | Azure Key 用 **DPAPI CurrentUser** 加密后存 JSON（Base64），不存明文 |
| 单文件发布 | `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true` |
| 安装包 | Inno Setup 6（`installer/SelectionTts.iss`） |
| 发布体积 | exe ≈ 66.4 MB（含 .NET 运行时）；安装包 ≈ 61.5 MB |
| 运行内存 | 空闲 ≈ 134 MB；3150 字长文峰值 ≈ 193 MB |

无第三方网络/UI 库，除 NAudio 外零 NuGet 依赖。P/Invoke 直接调用 `user32` / `crypt32` / `psapi` / `kernel32`。

---

## 3. 目录结构与职责

```
app/SelectionTts/
├── App.xaml / App.xaml.cs          应用生命周期、组件装配、热键分发、6 个自检入口 (535)
├── Core/
│   ├── ReaderService.cs            朗读编排：分句缓存、流式播放、seek、状态机 (775)
│   ├── TextSegmenter.cs            按句切分 + 句子边界回溯 (151)
│   ├── TextUtil.cs                 文本规范化（零宽字符、连字符换行、空白）、截断 (37)
│   ├── Settings.cs                 配置模型 + JSON 读写 + 归一化 (103)
│   ├── Maintenance.cs              「立即清理」：缓存/旧日志 + 内存归还 (104)
│   ├── AppPaths.cs                 便携 data 目录探测与回退 (39)
│   ├── Log.cs                      追加式日志，2 MB 轮转 (53)
│   └── VoiceCatalog.cs             音色/语速清单 (17)
├── Tts/
│   ├── EdgeTtsClient.cs            Edge WebSocket 流式合成 + 词时间戳解析 (292)
│   ├── DrmToken.cs                 Sec-MS-GEC v2 令牌（改版 SHA-256 + CS64） (275)
│   ├── AzureTtsClient.cs           Azure REST 合成，429 全局限流闸 (148)
│   └── TtsModels.cs                引擎接口与结果记录 (31)
├── Audio/
│   └── StreamingAudioPlayer.cs     连续 PCM 缓冲播放，真位置/真水位 (185)
├── Platform/
│   ├── HotkeyManager.cs            热键注册、增量换绑、交换让位、失败回滚 (238)
│   ├── HotkeyGesture.cs            组合键解析/序列化/录制（含 OEM 键） (178)
│   ├── HotkeyActions.cs            动作清单（加一行即多一个设置项） (21)
│   ├── ClipboardWatcher.cs         剪贴板事件监听、防抖、重试、去重 (160)
│   ├── ClipboardAccess.cs          只读剪贴板 + 隐私标记识别 (73)
│   ├── StaExecutor.cs              一次性 STA 线程执行 + 超时 (45)
│   ├── Dpapi.cs                    DPAPI 加解密 (109)
│   ├── AutoStart.cs                启动文件夹快捷方式 (53)
│   └── NativeMethods.cs            RegisterHotKey / 剪贴板监听 P/Invoke (24)
├── Tray/TrayIconManager.cs         托盘图标、菜单、气泡、灰度禁用态 (272)
└── Ui/
    ├── ReaderBoardWindow.xaml/.cs  朗读板：无焦点浮窗、折行、高亮、缩放、拖拽 (137/582)
    ├── BoardTextLayout.cs          自研折行（行 = 定位单元格） (114)
    └── SettingsWindow.xaml/.cs     设置页：引擎/密钥/热键/板尺寸/自启/维护 (77/370)
```

---

## 4. 运行时架构与数据流

```
┌─────────────┐   WM_CLIPBOARDUPDATE   ┌──────────────────┐
│ 用户复制文本 │ ─────────────────────► │ ClipboardWatcher │ 防抖100ms / 去重3s
└─────────────┘                        └────────┬─────────┘
                                                │ TextCopied(string)
                                                ▼
                                       ┌──────────────────┐
                                       │  ReaderService   │  分句 → 句级缓存 → 播放会话
                                       │  (generation /   │
                                       │   session 双代际)│
                                       └───┬──────────┬───┘
                        SynthesisLoop      │          │  PlaybackLoop
                    （优先 seek 目标，     │          │  （按句等待缓存，
                      顺序推进，回头补洞）  ▼          ▼    解码 PCM 写缓冲）
                            ┌──────────────┐    ┌────────────────────────┐
                            │ ISpeechSynth │    │ StreamingAudioPlayer   │
                            │ Edge / Azure │    │ WaveOut + 10s 缓冲     │
                            └──────────────┘    └────────────────────────┘
                                                │ PositionSeconds
                                                ▼
                         ┌───────────────────────────────────────┐
                         │ ReaderService.GetState() → ReadState  │
                         │ ReaderBoardWindow 每 150ms 轮询渲染    │
                         └───────────────────────────────────────┘

                         ┌──────────────────────────┐
                         │ HotkeyManager (WM_HOTKEY) │ → App 查表分发
                         │ TrayIconManager (菜单)    │ → SetWatching / TogglePause / …
                         └──────────────────────────┘
```

**线程模型**

- UI：WPF Dispatcher（STA），朗读板 150ms `DispatcherTimer` 轮询状态；不订阅高频事件，避免事件风暴。
- 合成：每个文本一个 `Task.Run(SynthesisLoopAsync)`，句内串行（服务端限流友好）。
- 播放：每次 seek/开始一个 `Task.Run(PlaybackLoopAsync)`，按句等待 `TaskCompletionSource<byte[]>`。
- 剪贴板：`System.Threading.Timer`，读剪贴板时切一次性 STA 线程（`StaExecutor`，1.2s 超时）。
- 热键：`HWND_MESSAGE` 消息窗口，`WM_HOTKEY` 同步回调后 `Dispatcher.Invoke` 回 UI。
- 托盘：WinForms `NotifyIcon`（与 WPF 共享消息泵）。

---

## 5. 功能清单与实现方式

### 5.1 剪贴板监听（复制即读）

- 事件驱动：`AddClipboardFormatListener` + `WM_CLIPBOARDUPDATE` 消息窗口（**非轮询**）。
- 100 ms 防抖合并：连续复制只处理最后一次。
- 读取失败重试：最多 3 次，间隔 120/180 ms（Excel、远程桌面延迟渲染）；仅「被占用」重试，无文本格式（图片/文件）不重试。
- 同文本 3 秒内不重复朗读（时间窗去重）。
- 关闭总开关期间复制的内容**不补读**；重新开启时把当前剪贴板记为基线，避免旧内容被当成新复制。
- 尊重隐私标记：`ExcludeClipboardContentFromMonitorProcessing`、`CanIncludeInClipboardHistory == 0` 的内容直接忽略（密码管理器约定）。
- 只读剪贴板，不做快照/还原（不修改用户内容）。
- 源码：`ClipboardWatcher.cs`、`ClipboardAccess.cs`、`StaExecutor.cs`。

### 5.2 文本预处理与分句

- `TextUtil.Normalize`：CRLF 归一、去除零宽字符（`\u200b/200c/200d/feff`）、合并行内多余空格/制表符、压缩 3 行以上空行为 2 行、去掉英文断词连字符换行。
- 截断：默认 5000 字（`MaxChars` 配置 50–20000），截断记日志并继续朗读。
- `TextSegmenter.SplitSentences`：以 `。！？；\n` 等硬断句切分，英文 `.` 仅在后接空白/行尾时断（避免切开 `3.14`、`file.txt`）；单句 >150 字时按 `，、：,` 等软断点降级切分。
- **关键设计**：合成的**最小单位是句子**，不是固定字数块。原因：段落边界 = 音频缓存单位，点击行跳读只需命中「该行所在句」，不必依赖时间戳（Azure 也没有时间戳）。
- 分句结果携带 `(Start, Length)`，与原文一一对应，保证「读到第几个字」能映射回视觉行，且拼接严格等于原文。

### 5.3 流式合成引擎

**接口**：`ISpeechSynthesizer.StreamAsync(text, voice, rate, onAudio, onBoundary, ct)`，音频分块回调、可选词边界回调，上层不感知引擎差异。

**Edge（默认，免费）**
- 自实现 WebSocket 协议（与 Python `edge-tts` 等价）：`speech.config`（仅开 wordBoundary）+ SSML 音节请求。
- 音频帧：2 字节大端头长度 + ASCII 头（`Path:audio`）+ MP3 负载；文本帧解析 `Path:turn.end` 结束、`Path:audio.metadata` 词时间戳。
- **Sec-MS-GEC v2 令牌**（`DrmToken.cs`）：`CS64(16 hex)-SHA256(64 hex)`，使用改版 SHA-256 常量表 + CS64 混淆；时间戳按 300 秒取整并加 Windows 纪元（11644473600s）。`ChromiumVersion` 常量需随 Edge 升级同步。
- 输出 `audio-24khz-48kbitrate-mono-mp3`，CBR 48 kbps → 时长 = 字节数 × 8 ÷ 48000。
- 重试策略：仅在**尚未吐出任何音频**时重试一次（已出声再重试会重复播放同一句）。
- 连接超时 10 s；请求头模拟 Edge 扩展（`Origin: chrome-extension://...`）。

**Azure（备用，需 Key）**
- REST 一次性返回整句 MP3；无词时间戳 → 高亮退化为**按字数比例**推进。
- 429 全局限流闸：记录 `RetryAfter`，所有请求共享等待；单请求最多 5 次尝试。
- 错误映射成人话：401 / 403 / 404 / 其他 HTTP 状态。
- Key 走 `Ocp-Apim-Subscription-Key` 头，UI 中 `PasswordBox` 显示，可切换明文预览。

### 5.4 朗读编排（核心）

`ReaderService` 是整条流水线的状态机，维护**双代际**：

- `_generation`：文本级。换文本/清空时 +1，旧合成与播放任务据此自杀。
- `_session`：播放级。每次 seek 时 +1，旧播放循环退出，避免两路音频同时写缓冲。
- 状态：`Idle / Preparing / Playing / Paused / Stopped / Completed / Failed`，粗粒度变化才发事件（托盘文案），进度靠 150 ms 轮询 `GetState()`。

**合成循环**（`SynthesisLoopAsync`）取句顺序：
1. `_jumpTarget`（seek 目标句，优先合成）
2. `_synthCursor` 起顺序向后
3. 到结尾后**回头补前面没合成的空洞**

**播放循环**（`PlaybackLoopAsync`）：
- 从 `startIndex` 顺序 `await segment.Ready.Task`（最多等一句合成时间）。
- MP3 → PCM（24 kHz/16 bit/单声道）→ 写 `BufferedWaveProvider`（背压：缓冲满每 40 ms 轮询等待，不丢音频）。
- 记录每句 PCM 起止字节，供「播放位置 → 字符位置」换算。
- 首声时把 `Preparing` 切到 `Playing`；若用户在这几十毫秒窗口内按了暂停，`Begin()` 后补一次 `Pause()`（竞态修补）。

**失败处理**：单句失败重试一次（间隔 600 ms），仍失败则缓存空音频并标记 `Failed`，**跳过该句继续读**，不让整篇挂掉；播放循环跳过零长度句。

**seek（点行跳读）**：`SentenceStart(text, charIndex)` 回退到句首 → 找所在句索引 → 新 session → 优先合成该句 → 命中缓存则秒开。

**进度与高亮**：
- 真实播放位置 = 已写入 PCM 字节 − 缓冲中剩余字节（不靠估算）。
- Edge：服务端词时间戳对齐回原文字符（`BuildWordSpans`，对不上退回游标）；Azure/无词数据：按句内比例推进。
- 总时长估算 = 已缓存句时长 + 未缓存字数 × 平均字时长。

**完成判定**：100 ms 定时器检查 `Playing && feederDone && player.IsDrained` → `Completed`（剩余未合成的句子继续后台补全，进度条补到 100% 但不朗读）。

**停止语义**：`Stop()` 保留文本与缓存、冻结位置（可从板子点行继续）；`Clear()`（总开关关闭）丢弃文本与全部音频缓存。

### 5.5 音频播放

- `WaveOutEvent`（200 ms 延迟）+ `BufferedWaveProvider`（24 kHz、16 bit、单声道、10 s 容量）。
- `ReadFully = true`：缓冲空时输出静音而不是触发 `PlaybackStopped`，否则块间空档会被误判「播完」。
- `DiscardOnBufferOverflow = false`：宁可背压也不丢音频。
- 写入按 `generation` 隔离：seek 后旧会话的写入直接丢弃。
- `PositionSeconds = (written - buffered) / BytesPerSecond`；`IsDrained = feederDone && buffered == 0`。

### 5.6 朗读板（置顶浮窗）

- **不抢焦点**：`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` + `ShowActivated=False`，复制时光标不跳。
- 自研折行 `BoardTextLayout`（`FormattedText` 逐行二分测量）：一行 = 一个 `TextBlock` = 一个可点击/高亮单元；英文尽量不切断单词；空行保留为段落间距。
- 高亮当前行（绿底白字）；用户手动滚动后 3 秒内不自动 `BringIntoView`，避免抢滚动条。
- 点任意行 → `JumpTo(line.Start)`（落到该行所在句首）。
- 进度条表示**合成进度**（全部缓存好即收起），不是播放进度。
- 头部状态点 + 状态文案：待命/合成百分比/朗读中/已暂停/完成/失败。
- 拖动移动（头部捕获鼠标，DPI 换算）；8 方向缩放用 `WM_NCHITTEST` 返回系统命中码（光标形状与跟手都是原生行为）。
- 字号自适应：`scale = clamp(Width / 420, 0.85, 2.15)`，基准字号 13.5 / 行高 21；拖宽后 180 ms 防抖重排。
- 尺寸持久化 500 ms 防抖；位置夹回虚拟桌面（多屏/分辨率变化后不会跑丢）；至少留 80×80 可见。
- 关闭（✕）只隐藏，不退出；总开关关闭时板子显示「已关闭」+ 当前热键提示，按钮变「启动」。
- 尺寸上下限：宽 360–900（上限再按当前工作区夹一次），高 ≥275。

### 5.7 托盘

- `NotifyIcon`，图标从 exe 提取；关闭态自动生成**灰度半透明**版本（`ColorMatrix` 0.3/0.59/0.11 + alpha 0.45）。
- 菜单：总开关（文案含当前热键）、显示朗读板（勾选态）、音色（4 个）、语速（4 档）、设置…、打开数据目录、打开日志、退出。
- 左键单击 = 显示/隐藏朗读板；**300 ms 节流**（双击会连发两次 `MouseClick`，不节流会「开了又关」）。
- 悬停提示 ≤63 字符（Win32 限制）；通知用气泡（2 s）。

### 5.8 全局热键

- 动作清单 `HotkeyActions.All`：加一行即自动出现在设置页并可注册、托盘/板子显示。
  - `toggle-watch`「开启/关闭朗读」默认 `Ctrl+Alt+R`
  - `toggle-pause`「暂停/继续朗读」**出厂留空**，由用户录制
- 注册：消息窗口 `RegisterHotKey` + `MOD_NOREPEAT`；id 单调递增；失败时**立刻**取 `GetLastWin32Error()`（中间再调别的 API 会被覆盖成 0）。
- **增量提交**：逐动作「注册新的 → 成功才释放旧的」，单个动作失败不影响其它动作；失败保留旧绑定。
- **交换让位**：A 的新键被 B 的旧键占着且 B 也要换键 → 先释放 B、A 注册、B 再注册；若 B 仍失败则恢复 B 的旧绑定（多次有限轮，不无限循环）。
- 错误码翻译：1409「已被其它程序占用」、5「权限不足」。
- 录制时挂起全部热键，保存后按新配置重新注册（不是恢复旧键，避免「保存了但要用到重启」）。
- 序列化格式 `Ctrl+Alt+R`；解析支持 `Ctrl/Control/Alt/Shift/Win/F1–F24`；OEM 单字符键 `, - = . /` 与虚拟键码双向映射（`0xBC/0xBD/0xBB/0xBE/0xBF`），保证录完重启不丢。
- 录制过滤：`Key.None/NoName/System/ImeProcessed/DeadCharProcessed` 与单独修饰键都不接受；Alt 组合用 `e.SystemKey`（WPF 的 `Key.System` 陷阱）。

### 5.9 设置页

- 合成引擎：Edge / Azure 单选，Azure 面板（区域、Key 密码框+明文预览切换、真实「测试合成」按钮）。
- 快捷键：按动作动态生成行（录制 / 默认清除）。
- 朗读板：显示当前尺寸 + 「恢复默认尺寸」（**保存时**才生效，取消不写）；说明默认/最小/最大。
- 启动：开机自启勾选（与启动文件夹实际状态同步）。
- 维护：「立即清理」+ 结果文本（清理量、内存前后值、被占用跳过数）。
- 保存流程（**顺序很关键**）：
  1. 结束录制态（不恢复热键）
  2. 校验 Azure 必填 → 校验热键撞键
  3. **校验全部通过后才改共享 `_settings`**（引擎/区域/DPAPI Key/板尺寸/热键）
  4. `SettingsStore.Save` → `AutoStart.Apply` → `ResumeHotkeys`（按新配置注册）→ 关闭
- 「取消」只关窗，不动配置与运行状态；窗口的 `SuspendHotkeys/ResumeHotkeys/Cleanup` 都由 `App` 注入，UI 与业务解耦。
- 为什么强调第 3 步：`_settings` 与朗读板/托盘共享实例。历史 bug：先改再校验，校验失败+取消后内存里留下未保存值，之后任何无关落盘（开关监听、拖板子、换音色）都会把它带进磁盘。现已修复，并有判别性测试覆盖。

### 5.10 配置与日志

- 配置 `data/config.json`（camelCase、缩进、大小写不敏感读取）。
- 归一化：未知音色→默认、非法语速→`+0%`、`MaxChars` 夹 50–20000、板尺寸只夹下限（上限运行时按屏幕算）、`Hotkeys` 兜底非空。
- 损坏配置：复制为 `config.json.bad` 后回退默认值，不静默丢失现场。
- **便携优先**：`data` 目录优先放在 exe 旁边；不可写（程序目录受保护）时回退 `%LOCALAPPDATA%\SelectionTts`。写探针 `.write-test` 判定。
- 日志：`data/logs/app.log`，带锁追加；启动时 >2 MB 轮转为 `app.old.log`（覆盖旧的）；写日志失败静默，绝不影响主流程。

### 5.11 开机自启

- 在当前用户「启动」文件夹放 `剪贴板朗读.lnk`（经 `WScript.Shell` COM 创建），不走注册表 `Run`：
  - 用户可自行删除、任务管理器可见；
  - 与安装包自启任务用同一文件名，不会出现两份自启。

### 5.12 维护 / 立即清理（v1.0.1 新增）

- 删除 `data/cache/*` 与 `logs/app.old.log`（**保留当前 `app.log`**），逐文件容错（被占用则跳过并计数）。
- 内存：`LargeObjectHeapCompactionMode = CompactOnce` + 两次 `GC.Collect`（含压缩、finalizer）→ `EmptyWorkingSet`（psapi）把空闲页还给系统。
- 返回 `FreedBytes / Files / Failed / MemoryBeforeMb / MemoryAfterMb` 并写日志；UI 展示「已清理 X KB（N 个文件）· 内存 A → B MB」/「缓存已是空的」。
- **刻意不清当前朗读的音频缓存**（那是点行跳读的数据基础），说明文案明示「不影响当前朗读」。
- 不做系统级待机列表清理（需要管理员、会踢掉其它程序的缓存页），不做自动清理。

### 5.13 单实例与生命周期

- `Mutex(@"Local\SelectionTts.SingleInstance")`：同用户会话只允许一个实例，第二个直接退出（记日志）。
- `ShutdownMode=OnExplicitShutdown`：隐式关窗不退出；退出只走托盘「退出」。
- `OnExit` 顺序释放：watcher → hotkeys → reader → board → tray → mutex。
- 命令行：`--settings`（启动即开设置）、`--watch`（强制开启监听）；`--selftest-*` 六个自检开关（见第 8 节）。

### 5.14 安装包

- Inno Setup 6：`PrivilegesRequired=lowest`（按用户安装）、`MinVersion=10.0`、`x64compatible`、LZMA2 最大压缩。
- 附加任务：开机自启（启动文件夹快捷方式）、桌面图标（默认不勾）。
- 卸载时默认**保留** `data`（设置/日志），仅在用户明确确认时删除。
- 安装文件：单文件 exe + README。

---

## 6. 关键不变量（审查时请重点验证是否被破坏）

1. **热键不变量**：任意时刻 `_current` 中各动作的组合键互不相同；某动作注册失败时其**旧绑定仍生效**；`_nextId` 单调递增不复用。
2. **双代际隔离**：任何后台任务（合成/播放）写状态前必须校验 `generation`（文本级）与 `session`（播放级）；过期任务只能退出，不得改 `_plan/_status/_charIndex`。
3. **缓存生命周期**：句级缓存只在「换文本 / Clear / 进程退出」时整体丢弃；`Stop()` 与 seek **不得**丢缓存；seek 必须在句首对齐。
4. **单路播放**：同一时刻只有一个播放循环在写音频缓冲（由 `session` + `player.Begin()` 的 generation 双重保证）。
5. **共享设置实例**：`Save()` 必须「先校验、后修改」；`_boardSizeReset` 只在 `Save()` 内写回默认尺寸。
6. **AI 无焦点**：朗读板的显示/更新/点击不得夺走前台窗口焦点（`WS_EX_NOACTIVATE`）。
7. **UI 轮询而非事件风暴**：朗读板 150 ms 轮询；`StatusChanged` 只在粗粒度状态变化时触发。
8. **失败降级**：单句合成失败不终止整篇；单文件清理失败不终止清理；写日志失败不影响主流程。
9. **隐私**：剪贴板内容只读不写；带排除标记的内容不读；Azure Key 不落明文、不进日志。
10. **UTF-8/转义**：SSML 与 XML 属性统一转义 `& < > " '`；配置读写显式 UTF-8。

---

## 7. 已知限制与刻意取舍（不是 bug，但审查时可评估是否值得改）

| 项 | 说明 | 取舍理由 |
|---|---|---|
| Edge 接口非官方 | 依赖 `Sec-MS-GEC` 令牌与 Chromium 版本常量；Edge 升级后可能失效，需同步 `DrmToken.ChromiumVersion` 与常量表 | 免 Key、免费、延迟低；单点维护成本 |
| Azure 无词时间戳 | REST 接口不返回 boundary，高亮按句内字数比例推进，长句内可能轻微漂移 | Azure 只是备用引擎 |
| seek 粒度为「句」 | 点一行会从该行所在**句首**开始，而不是该行第一个字 | 无时间戳也能秒开；句首对齐听感更自然 |
| 无句内断点续读 | `Stop()` 后只能从某行（句首）重新开始，不能停在句中字位继续 | 音频按句缓存，句内无切割点 |
| 只吃剪贴板 | 不做全局选区抓取、不读屏幕、不注入其它进程 | 零侵入、权限低、实现简单 |
| 文本上限 | 默认 5000 字（可配 50–20000），整篇音频缓存在内存 | 避免内存失控；长文截断有日志 |
| 记忆体 | 当前文本的全部音频常驻内存（LOH），3150 字约 190 MB 峰值 | 支撑「点行秒跳」；清理按钮可归还空闲页 |
| 无自动更新 | 版本更新需手动重装 | 无签名基础设施 |
| 单实例按会话 | `Local\` 互斥量，同一用户会话唯一 | 避免多实例抢热键/剪贴板 |
| 音色/语速固定清单 | 4 音色 × 4 档语速，不支持任意 SSML 参数 | UI 极简 |

---

## 8. 自检与测试现状

项目没有单元测试工程，验证方式是**日志断言 + UI 自动化实测**：

```
SelectionTts.exe --selftest-drm        # Sec-MS-GEC 令牌生成
SelectionTts.exe --selftest-tts        # Edge 合成 + 解码 + 播放
SelectionTts.exe --selftest-stream     # 首包延迟、词时间戳数量/单调性/末条与总长吻合
SelectionTts.exe --selftest-pipeline   # 首声延迟、分块推进、文本抢占、停止响应
SelectionTts.exe --selftest-config     # DPAPI 往返、热键注册/换绑/冲突回滚/交换/OEM 往返、配置往返、Azure 401 提示
SelectionTts.exe --selftest-autostart  # 自启开→关→复原，快捷方式随状态出现/消失
```

已通过人工 + UI 自动化验证的场景（v1.0.1 回归）：

- 8 方向缩放（真实鼠标拖动）；字号随宽度变化并限幅
- 设置页撞键保存被拦截；「恢复默认尺寸」保存后板子实时 420×320、重启保持
- 设置校验失败 + 取消后，无关落盘**不**带出脏数据（判别性测试）
- 热键交换（A↔B 互换）成功；OEM 键 `, - = . /` 重启后往返一致
- 托盘左键单击开关板子（300 ms 节流，真实点击）
- 朗读中执行「立即清理」：合成不中断、播放连续；空缓存重复点击提示「缓存已是空的」
- PowerShell/Python 原型已弃用，仅保留 C# 实现

---

## 9. 建议的审查重点（给审查者）

按风险从高到低：

1. **并发正确性**（`ReaderService.cs`）
   - `_gate` 锁内/锁外的边界是否一致；`_plan` 在锁外被读取的地方是否会与重置竞争。
   - `TaskCompletionSource.Ready` 的取消/完成路径：`Fail()` 中 `TrySetCanceled` 后，播放循环 `await Ready.Task.WaitAsync(ct)` 是否总是能退出。
   - `PlaybackLoopAsync` 开头捕获 `plan` 引用后，`_plan` 被替换为新的 List 是否会影响旧循环（应为只读旧列表，但请确认所有字段更新都在锁内且校验代际）。
   - `CheckCompletion` 定时器与 `Dispose` 的先后顺序（`_timer.Dispose()` 后是否有回调仍在访问状态）。
2. **HotkeyManager 部分失败的组合爆炸**
   - `TryApply` 的 make-room 循环在「三动作互换」「A 想抢 B、B 想抢 C、C 想抢 A」时是否收敛；`Restore` 失败后是否可能丢失某个热键而不报错。
   - 部分失败时返回值与错误的语义：是否会「注册了一半但用户以为全成功」。
3. **Edge 协议健壮性**（`EdgeTtsClient.cs` / `DrmToken.cs`）
   - 大帧/分片 `EndOfMessage` 处理、`MemoryStream.GetBuffer()` 与长度截断、网络半关闭。
   - 令牌时间窗（300 s 取整）与服务端时钟偏差容忍度；`ChromiumVersion` 与 `Sec-MS-GEC-Version` 是否必须严格一致。
   - 重试逻辑：`emitted` 标志用 `Interlocked.Exchange` 只置 1，是否覆盖「回调抛异常导致部分写入」的边界。
4. **线程与 COM/STA**
   - `WaveOutEvent` 在线程池线程创建（`PlaybackLoopAsync` 内 `player.Begin()`）：长时间运行是否稳定。
   - `StaExecutor` 每次读剪贴板新建线程 + `Join(timeout)`：超时后线程仍在跑，是否可能泄漏/访问已释放对象。
   - 托盘 `NotifyIcon`（WinForms）与 WPF Dispatcher 共用消息泵的关闭顺序（`Dispose` 在 `OnExit`）。
5. **UI/DPI/多显示器**
   - `WM_NCHITTEST` 的 `short` 解包（负坐标）、`PointFromScreen` 的 DIP 换算、`ResizeBorder = 5` 在 200% 缩放下是否过窄。
   - `ClampToVirtualScreen` 在窗口尚未 `Show()`（`ActualWidth = 0`）时的取值。
6. **设置与持久化**（`SettingsWindow.xaml.cs` / `Settings.cs`）
   - 撞键校验用了 `Dictionary<HotkeyGesture,string>`（record struct 值相等）——请确认 OEM 键与普通键不会因 `Modifiers` 含 `MOD_NOREPEAT`（0x4000）而产生「看似相同实为不同」的比较。
   - 保存时 `ResumeHotkeys` 重新注册的理论失败路径：保存成功后热键却注册失败，UI 已关闭，用户只收到气泡——是否需要更强提示。
   - `SettingsStore.Save` 非原子写（直接 `File.WriteAllText`）：断电/崩溃时可能留下半截 JSON（有 `.bad` 兜底但会丢设置）。
7. **安全**
   - DPAPI 的 `CryptProtectData` 参数（`CryptProtectUiForbidden`）与 `LocalFree` 成对性；解密失败返回空串是否会让「配置了 Azure」静默回退 Edge（日志有 WARN）。
   - 日志是否会间接记录 Key/完整剪贴板内容（抽查 `Log.Info` 调用点）。
   - Edge 令牌算法属于公开逆向实现，无账号凭据；但 `Cookie: muid=<GUID>` 每次随机是否必要。
8. **文本处理边界**
   - `TextUtil.Truncate` 用 `text[..maxChars]` 按 UTF-16 code unit 截断，可能切开代理对（emoji）→ 建议评估。
   - `BoardTextLayout` 按 `char` 二分测量，组合字符/代理对可能被切成半个（渲染异常而非崩溃）。
   - `TextSegmenter` 的英文句号规则在 `Mr. Smith`、`3.14 ` 等场景的误判面。
9. **资源与内存**
   - 每句 `byte[] Mp3` + PCM 解码副本的峰值；5000 字约多少句、内存是否可控。
   - `BufferedWaveProvider` 10 s 缓冲在 24 kHz/16 bit 单声道下约 480 KB，`DiscardOnBufferOverflow=false` 的背压是否可能死等（播放器暂停时写入是否会永久阻塞）。
10. **安装/便携路径**
    - `PrivilegesRequired=lowest` + `DefaultDirName={autopf}` 的实际落点（按用户安装时 `{autopf}` 指向用户目录）；自启快捷方式与 `AutoStart.cs` 的路径是否一致（都指向 `{userstartup}`，但文件名需一致才能去重）。

---

## 10. 如何构建与运行

```powershell
# 开发构建
cd app/SelectionTts
dotnet build -c Debug

# 运行（Debug 输出目录）
.\bin\Debug\net10.0-windows\SelectionTts.exe

# 自检（结果写 data/logs/app.log）
.\bin\Debug\net10.0-windows\SelectionTts.exe --selftest-config

# 发布单文件（Release）
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o publish/win-x64

# 安装包
& "C:\Users\Administrator\AppData\Local\Programs\Inno Setup 6\ISCC.exe" installer/SelectionTts.iss
```

运行数据默认在 exe 旁 `data/`（不可写时回退 `%LOCALAPPDATA%\SelectionTts`）。

---

## 11. 一句话总结

> 一个 5,000 行的 WPF 托盘工具：自实现 Edge 流式 TTS 协议 + 句级缓存流水线 + 不抢焦点的逐行朗读板，核心复杂度集中在**双代际并发的朗读状态机**与**热键换绑的失败恢复**；功能面收敛、依赖极少、隐私边界清晰。审查时最值得投入的是 `ReaderService` 的并发不变量与 `HotkeyManager` 的部分失败语义。
