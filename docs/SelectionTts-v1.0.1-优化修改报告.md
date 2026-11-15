# SelectionTts v1.0.1 优化修改报告

> 审查日期：2026-11-16  
> 审查基线：`D:\桌面\TTS` 当前工作区  
> 备份核对：`D:\桌面\SelectionTts_beta\v1.0.1` 与当前工作区的源码、XAML、工程文件、安装脚本、文档及图片资源逐文件 SHA-256 一致。  
> 适用对象：OpenCode 或后续接手开发的工程师。

## 1. 结论

SelectionTts 的产品方向、交互模型和总体架构是合理的，不需要推倒重写。

当前应优先解决的是：

1. 播放异常时的状态恢复。
2. seek/stop/begin 过程中的并发竞态。
3. 设置保存和热键注册的事务性。
4. 不符合“低资源占用”目标的伪优化。
5. 缺少可重复的自动测试。

这些问题会直接影响用户对“稳定、轻量、专业”的判断，应优先于新增功能。

审查时 `dotnet build -c Debug --no-restore` 已通过，0 警告、0 错误。由于 v1.0.1 备份与开发目录源码完全一致，本报告中的问题同样存在于备份版本。

---

## 2. 修改原则

OpenCode 修改本工程时必须遵守以下原则：

1. 优先修复正确性和资源稳定性，不先做大规模重构。
2. 每次修改保持范围清晰，避免一次性重写整个 `ReaderService`。
3. 保留现有用户配置格式；如必须迁移，提供向后兼容。
4. 不引入大型 UI 框架、ORM、数据库、事件总线等与当前规模不匹配的依赖。
5. 不使用 `GC.Collect()`、`EmptyWorkingSet()` 作为常规性能优化。
6. 所有并发修复必须能通过确定性测试或故障注入测试证明。
7. 不把真实网络、真实剪贴板、真实全局热键和真实用户配置作为自动测试的前置条件。
8. 修改前检查工作区已有改动，不执行 `git reset --hard`、`git checkout --` 等破坏性操作。

建议目标版本：`v1.0.2`。

---

## 3. 高优先级问题

### P1-1 播放器初始化失败会让朗读状态永久卡住

位置：

- `app/SelectionTts/Core/ReaderService.cs`
- `PlaybackLoopAsync`
- 约第 578 行开始，`_player.Begin()` 约在第 605 行
- `try/catch` 约在第 618 行才开始

问题：

- `_player.Begin()` 在异常处理边界之外。
- 无音频设备、设备被独占、`WaveOutEvent.Init()` 失败、`Play()` 失败时，后台任务会直接 fault。
- ReaderService 不会调用 `Fail(...)`，界面仍显示“正在合成”或“正在朗读”。
- 异常任务没有被观察，日志里也可能没有记录。

修改方向：

1. 将播放器创建、`Init`、`Play` 和后续播放循环全部放入统一异常边界。
2. 捕获异常后校验 generation/session，再调用 `Fail(generation, ex)`。
3. `StreamingAudioPlayer.Begin()` 内部在 `Init()` 或 `Play()` 失败时释放已经创建的 `WaveOutEvent`。
4. 增加“无可用音频设备”的故障注入测试。

验收标准：

- 注入一个必然在 `Begin()` 抛异常的 fake player 后，状态进入 `Failed`。
- 界面显示可理解的失败信息，不永久停留在 `Preparing`。
- 后台任务不再产生未观察异常。

### P1-2 seek 时旧播放会话可能污染新会话进度

位置：

- `app/SelectionTts/Audio/StreamingAudioPlayer.cs`
- `WritePcmAsync` 约第 79 行
- `buffer.AddSamples()` 和 `Interlocked.Add()` 约第 108-109 行

问题：

`WritePcmAsync` 在锁内校验 generation 并取得 `_buffer`，随后在锁外执行：

```csharp
buffer.AddSamples(pcm, offset, count);
Interlocked.Add(ref _writtenBytes, count);
```

如果这期间发生 `Stop()`、`Begin()` 或 seek：

- generation 已经变化。
- `_writtenBytes` 可能已经被新会话重置。
- 旧流水线仍会把字节写入旧 buffer，并把数量累加到新会话的计数器。
- 最终会导致位置、进度、剩余时间和高亮偏移。

修改方向，二选一：

方案 A，最小修复：

- 将 generation 校验、`AddSamples`、`_writtenBytes` 更新放进同一个同步边界。
- 写入时再次确认当前会话 id 没有变化。

方案 B，推荐修复：

- 将一次播放会话封装成不可混淆的 `PlaybackSession` 对象。
- session 内独立保存 buffer、generation、written bytes、feederDone。
- 所有写入、暂停、完成判断、位置读取都携带 session id。
- `MarkFeederDone` 也必须携带 generation，不能在旧循环结束时无条件修改新播放器状态。

验收标准：

- 快速连续执行 `Begin -> Write -> Stop -> Begin` 时，新会话的已写字节只包含新会话数据。
- seek 后 `PositionSeconds` 不跳变、不为负、不继承旧会话数据。
- 增加“旧写入跨越 session 切换”的并发回归测试。

### P1-3 设置保存可能“假成功”

位置：

- `app/SelectionTts/Core/Settings.cs`
- `SettingsStore.Save` 约第 94 行
- `File.WriteAllText` 约第 100 行
- `app/SelectionTts/Ui/SettingsWindow.xaml.cs`
- 保存流程约第 417-423 行

问题：

- `SettingsStore.Save` 捕获全部异常，但没有返回值。
- 磁盘满、权限变化、文件被占用或 JSON 序列化失败时，调用方无法知道保存失败。
- 设置窗口仍会执行 `Saved = true` 并关闭，用户会误以为配置已保存。
- 直接覆盖 `config.json` 不是原子写入，进程崩溃或断电可能留下半截 JSON。
- `Load` 虽然有 `.bad` 备份，但会把用户推回默认配置。

修改方向：

1. `Save` 返回明确的成功/失败结果，例如 `bool` 或 `SettingsSaveResult`。
2. 写入同目录临时文件。
3. `Flush` 后使用原子替换或带备份的安全替换。
4. 只有保存成功后才设置 `Saved = true` 并关闭窗口。
5. 失败时保留设置窗口，显示具体错误，不修改当前生效配置。
6. 不要依赖调用方轮询日志判断失败。

建议顺序：

```text
校验草稿
-> 尝试应用热键
-> 原子保存草稿
-> 成功：提交共享设置并关闭窗口
-> 失败：恢复旧热键，保留窗口，显示错误
```

如果实现成本过高，至少保证：

- 保存失败返回 false。
- 窗口不关闭。
- 日志和 UI 都有错误信息。

验收标准：

- 模拟只读目录、磁盘异常或替换失败时，窗口保持打开。
- 失败后当前运行配置不变化。
- 正常保存后重启能读取同一份配置。
- 写到一半强制终止进程不会破坏上一份有效配置。

### P1-4 热键部分成功会导致配置与运行状态不一致

位置：

- `app/SelectionTts/Platform/HotkeyManager.cs`
- `TryApply`
- `app/SelectionTts/App.xaml.cs`
- `ApplyHotkeys`
- `app/SelectionTts/Ui/SettingsWindow.xaml.cs`
- `ResumeHotkeys?.Invoke()`

问题：

- `TryApply` 在部分动作注册成功、部分失败时返回 false。
- 成功动作已经切换到新键，失败动作可能暂时保留旧键。
- 但设置文件已经写入“全部为新配置”。
- SettingsWindow 仍然关闭，只靠托盘气泡提示。
- 重启后旧键不存在，用户会遇到“当前按键和重启后按键不同”。

修改方向：

推荐整批事务：

1. 对当前热键状态做快照。
2. 尝试注册全部新配置。
3. 任意一项失败则恢复全部旧绑定。
4. 返回结构化结果，包含失败动作、组合键和系统错误码。
5. 设置窗口在失败时保持打开，让用户修正冲突。

如果坚持“部分成功”语义：

- 配置必须记录实际生效状态，而不是记录预期状态。
- UI 必须明确列出哪些动作成功、哪些失败。
- 重启后行为必须可预测。

验收标准：

- A、B 互换成功。
- A 想抢 B 的键、B 不回退时，整批失败或明确部分成功。
- 外部程序占用某组合键时，所有旧绑定最终仍可用。
- 保存失败后，配置和当前运行状态一致。

---

## 4. 中优先级问题

### P2-1 剪贴板慢读取期间可能丢失最新复制

位置：

- `app/SelectionTts/Platform/ClipboardWatcher.cs`
- `ReadLatest` 约第 81 行

问题：

当 `_busy == 1` 时直接返回，没有记录“又发生了一次更新”。前一次读取若较慢，期间的新复制可能永远不会被再次读取。

修改方向：

- 增加 pending/latest 标记，忙结束后再读取一次。
- 更推荐改成单消费者队列，只保留最新文本。

验收标准：

- 模拟第一次读取阻塞 2 秒，期间连续复制 A、B、C。
- 恢复后最终只朗读 C。
- 不重复朗读 A、B。

### P2-2 每次剪贴板读取都创建 STA 线程

位置：

- `app/SelectionTts/Platform/StaExecutor.cs`

问题：

- 每次读取、每次重试都可能新建 OS 线程。
- `Join(timeout)` 超时后线程没有取消机制，仍可能继续访问剪贴板。
- 高频复制或异常剪贴板提供者会放大线程和句柄数量。

修改方向：

- 建立一个长期存活的 STA 工作线程。
- 使用 `BlockingCollection`、`Channel` 或等价队列。
- 队列只保留最新任务；旧任务可安全标记为 superseded。
- 支持取消、超时和进程退出。

验收标准：

- 连续 1000 次剪贴板更新后，线程数不持续增长。
- 超时任务不会与新任务并发访问剪贴板。
- 退出时 STA 线程能被可靠结束。

### P2-3 Edge TTS 接收阶段没有空闲超时

位置：

- `app/SelectionTts/Tts/EdgeTtsClient.cs`
- 连接超时约第 130 行
- 接收循环约第 140 行

问题：

- 只有 10 秒连接超时。
- 连接成功后，服务端可以无限期不返回下一帧。
- 当前句可能长期卡在合成中，后续句子无法推进。

修改方向：

- 每个请求设置总超时。
- 每次成功收到帧后重置“空闲超时”。
- 空闲超时后按句级失败处理，继续现有重试/跳过流程。
- 对单条 WebSocket 消息设置合理大小上限，避免异常大帧导致内存暴涨。

验收标准：

- 模拟连接成功但不发数据，超时后状态可恢复。
- 模拟发一半后停住，不永久占用合成循环。

### P2-4 多显示器负坐标位置会丢失

位置：

- `app/SelectionTts/Ui/ReaderBoardWindow.xaml.cs`
- `RestorePosition` 约第 642 行
- `IsOnScreen` 约第 677 行

问题：

- 代码把任何 `left < 0` 或 `top < 0` 视为无效。
- Windows 多显示器布局中，主屏左侧或上方的显示器使用负坐标。
- 朗读板移动过去后，重启会跳回主屏。
- 当前 `-1` 同时被用作“未设置”的哨兵值，与合法负坐标冲突。

修改方向：

- 使用 `int?`、`int.MinValue` 或显式的 `HasSavedPosition` 表示未设置。
- 根据虚拟桌面矩形和显示器工作区判断位置。
- 必要时枚举 `Screen.AllScreens`，选择与保存点最近的显示器。
- 迁移旧配置时，将 `-1` 视为未设置。

验收标准：

- 显示器位于主屏左侧时，负 X 坐标可以保存和恢复。
- 显示器位于主屏上方时，负 Y 坐标可以保存和恢复。
- 拔掉副屏后，窗口回到最近可用显示器。

### P2-5 Unicode 代理对可能被截断或拆行

位置：

- `app/SelectionTts/Core/TextUtil.cs`
- `Truncate` 约第 37-44 行
- `app/SelectionTts/Ui/BoardTextLayout.cs`
- 二分测量和 `Substring` 约第 55、90、104、116 行

问题：

- 按 UTF-16 code unit 直接截取，可能切开代理对。
- 自研折行二分可能落在代理对中间。
- emoji、部分生僻字和组合字符可能显示异常。

修改方向：

1. 截断时向前回退到完整文本元素或 Rune 边界。
2. 折行计算后校验不落在低代理项。
3. 对组合字符可逐步引入 `StringInfo`/文本元素簇。
4. 保持现有 `Start/Length` 以 UTF-16 索引保存，避免大范围改数据模型。

验收标准：

- 截断位置刚好落在 emoji 中间时，不产生半个代理对。
- 折行时 emoji 不会被拆成两行。
- 中英文混排、组合音标、国旗 emoji 不崩溃。

### P2-6 “立即清理”会阻塞 UI，且内存优化具有误导性

位置：

- `app/SelectionTts/Core/Maintenance.cs`
- `TrimMemory` 约第 88-99 行
- `app/SelectionTts/Ui/SettingsWindow.xaml.cs`
- `RunCleanup` 约第 94-106 行

问题：

- 清理按钮在 UI 线程同步执行。
- 连续两次阻塞式完整 GC 可能造成可感知卡顿。
- `EmptyWorkingSet` 只是把页面移出工作集，不降低进程真实内存需求。
- 页面之后仍会被访问，可能增加缺页和重新加载成本。
- 这不符合“低性能占用”的目标。

修改方向：

1. 删除常规流程中的强制 GC 和 `EmptyWorkingSet`。
2. “立即清理”只清理磁盘缓存和旧日志。
3. 清理操作改为异步，并保持按钮禁用/进度状态。
4. 如需要诊断内存，单独提供“诊断模式”，不作为用户优化按钮。
5. `FreedBytes` 只累计真正删除成功的文件。

验收标准：

- 点击清理时 UI 不冻结。
- 清理不触发强制完整 GC。
- 当前朗读不中断。
- 重复点击不会创建一堆并发清理任务。

### P2-7 卸载器可能漏掉 LocalAppData 数据

位置：

- `app/SelectionTts/installer/SelectionTts.iss`
- `CurUninstallStepChanged` 约第 53-69 行
- `app/SelectionTts/Core/AppPaths.cs`

问题：

- 应用优先使用 `{app}\data`。
- 不可写时回退到 `%LOCALAPPDATA%\SelectionTts`。
- 卸载器只检查 `{app}\data`。
- 回退目录会残留配置、日志和缓存。

修改方向：

- 卸载时检查两个路径：
  - `{app}\data`
  - `{localappdata}\SelectionTts`
- 默认保留用户数据。
- 用户明确选择删除时，分别处理存在的数据目录。
- 静默卸载默认保留。

验收标准：

- 安装目录可写时，卸载提示能处理 `{app}\data`。
- 安装目录不可写、实际使用 LocalAppData 时，卸载提示对应真实目录。
- 默认拒绝删除后数据仍然存在。

### P2-8 第二实例退出时可能错误释放未持有的 Mutex

位置：

- `app/SelectionTts/App.xaml.cs`
- 单实例检查约第 69-73 行
- `OnExit` 约第 623 行

问题：

- 第二个实例 `createdNew == false`。
- 它没有取得 Mutex 所有权，但 `OnExit` 仍调用 `ReleaseMutex()`。
- 可能抛出 `ApplicationException`，产生不必要的错误或崩溃。

修改方向：

- 增加 `_ownsInstanceMutex`。
- 只有成功取得所有权时才调用 `ReleaseMutex()`。
- 第二实例只执行 `Shutdown()`。

验收标准：

- 连续启动两个实例，第二个正常退出。
- 第一个实例的 Mutex 所有权不受影响。
- 退出日志无异常。

### P2-9 引擎、音色和语速可能在朗读中途变化

位置：

- `app/SelectionTts/Core/ReaderService.cs`
- `SetEngine` 约第 125 行
- `_engine`、`_settings.Voice`、`_settings.Rate` 使用约第 566-569 行
- `app/SelectionTts/Tray/TrayIconManager.cs`
- 托盘切换音色/语速

问题：

- `_engine` 和共享 `AppSettings` 没有在开始朗读时快照。
- 朗读中的剩余句子可能使用新引擎、新音色或新语速。
- 这与“当前文本使用同一套参数”的用户预期不一致。
- 也与现有“切换引擎下一条朗读生效”的注释不一致。

修改方向：

- 在 `StartNew` 时生成不可变的 `ReadingOptions`：
  - Engine
  - Voice
  - Rate
  - MaxChars
  - 其他影响合成的参数
- 本次朗读所有句子只读取该快照。
- 新设置的音色/语速/引擎只影响下一次复制或显式重新开始。

验收标准：

- 长文朗读到一半切换音色，当前文本保持原音色。
- 下一次复制使用新音色。
- 切换 Azure/Edge 不会让同一篇文本中西合璧地换引擎。

### P2-10 未使用的 EdgeProxy 配置应删除或实现

位置：

- `app/SelectionTts/Core/Settings.cs`
- `EdgeProxy` 约第 24 行

问题：

- 配置模型存在该字段，但全工程没有使用。
- 用户或后续开发者会误以为代理可用。

修改方向：

- 当前没有代理 UI 和需求时删除字段。
- 如果要保留，明确实现 `ClientWebSocket.Options.Proxy`，并增加设置项与测试。

---

## 5. 性能与存储优化方向

### 5.1 改用流式 MP3 解码，降低峰值内存

位置：

- `app/SelectionTts/Audio/StreamingAudioPlayer.cs`
- `DecodeMp3` 约第 57-75 行

当前流程：

```text
读取完整 MP3
-> MemoryStream 预分配 mp3.Length * 6
-> 解码完整 PCM
-> ToArray 再复制一份
-> 才写入 BufferedWaveProvider
```

问题：

- 每句至少产生两份额外 PCM 内存。
- PCM 很可能进入 LOH，增加 GC 压力和峰值。
- 首次出声要等整句解码完成。

优化方向：

- 使用 `Mp3FileReader` 分块读取。
- 直接将每次读取的 16-64 KB PCM 写入播放缓冲。
- 使用 `ArrayPool<byte>` 或复用工作区。
- 保持句子边界和缓存语义不变。
- 只在需要完整时长时读取流长度或累计实际 PCM 字节。

验收标准：

- 同一 5000 字文本下私有内存峰值下降。
- 首声延迟不增加，最好下降。
- seek 后播放行为不变。
- 音频无断层、无丢帧。

### 5.2 不再把工作集下降当成“内存优化”

建议度量：

- `Process.PrivateMemorySize64`
- `GC.GetTotalMemory(false)`
- LOH 大小与 GC 次数
- 线程数
- 句柄数

不建议只用：

- 任务管理器的工作集
- `EmptyWorkingSet`

工作集下降不代表真实占用下降，且可能导致下一次操作变慢。

### 5.3 发布体积策略

当前 60 MB 左右安装包的主要来源是自包含 .NET/WPF 运行时，不是业务代码。

建议拆成两个发布通道：

1. 标准版：继续自包含单文件，普通用户零依赖。
2. Lite 版：框架依赖发布，要求安装 .NET 10 Desktop Runtime，体积显著更小。

可评估项：

- 如果很在意临时目录占用，提供文件夹版，避免 native 库自解压。
- 不建议对 WPF 使用 trimming 或 NativeAOT，维护成本高且风险大。
- 如果产品硬指标是空闲内存低于 80 MB，应认真评估替换 WPF；继续微调 GC 不现实。

### 5.4 保持当前合理的资源上限

以下设计可以保留：

- 默认 5000 字上限。
- 日志当前文件 + 旧文件最多约 4 MB。
- 句级缓存不落盘。
- 音频只缓存在内存。
- 拖拽保存尺寸使用 500 ms 防抖。
- 朗读板 150 ms 轮询。

这些不是当前性能问题的主要来源，不应为了“看起来轻量”而破坏交互体验。

---

## 6. 测试与自检改造

当前没有单元测试工程，六个 `--selftest-*` 主要依赖日志人工判断，部分还会修改真实配置、真实自启动项，不适合 CI。

### 6.1 新增测试工程

建议新增：

```text
SelectionTts.Tests/
```

优先测试纯逻辑和可注入组件：

- `TextUtil`
- `TextSegmenter`
- `BoardTextLayout`
- `HotkeyGesture`
- `SettingsStore`
- `HotkeyManager`
- `StreamingAudioPlayer`
- `ReaderService`

### 6.2 必要抽象

为了测试，建议引入最小接口：

```csharp
public interface IAudioPlayer
{
    int Begin();
    Task WritePcmAsync(ReadOnlyMemory<byte> pcm, int generation, CancellationToken ct);
    void MarkFeederDone(int generation);
    void Pause();
    void Resume();
    void Stop();
    double PositionSeconds { get; }
    bool IsDrained { get; }
    void Dispose();
}
```

热键部分建议抽出：

```csharp
public interface IHotkeyRegistrar
{
    bool Register(int id, HotkeyGesture gesture, out int errorCode);
    void Unregister(int id);
}
```

不要为了测试引入复杂 DI 容器，构造函数注入即可。

### 6.3 必测场景

并发与状态：

- 新文本抢占旧文本。
- seek 到尚未合成的句子。
- seek 到已缓存句子。
- Pause/Resume 在 Begin 前后发生。
- Stop 后保留缓存，再次 seek 能继续。
- 播放设备初始化失败。
- 合成器永久挂起后取消。
- 单句失败后跳过并继续。

剪贴板：

- 慢读取期间连续复制，只保留最新。
- 剪贴板被占用后重试成功。
- 隐私标记内容不读取。
- 关闭监听期间的内容不补读。

热键：

- 正常换绑。
- A/B 互换。
- 三动作循环交换。
- 外部冲突时全量回滚。
- OEM 键往返。
- 清空绑定。

配置：

- 原子保存。
- 损坏 JSON 恢复。
- 保存失败不修改当前状态。
- 旧配置兼容。

文本：

- emoji 截断。
- emoji 折行。
- 中英文混排。
- `Mr. Smith`、`3.14`、`file.txt`。
- 超长单句软切分。

### 6.4 自检入口改造

自检应使用临时数据目录：

```text
%TEMP%\SelectionTts.Tests\<run-id>\
```

不得修改：

- 当前用户真实 `config.json`
- 当前用户真实启动文件夹
- 当前用户真实日志

联网测试必须显式标记为 manual/network test，不进入默认 CI。

---

## 7. 建议实施顺序

### 阶段 0：建立安全基线

1. 创建 `v1.0.2` 分支。
2. 增加测试工程和 fake player/fake synthesizer。
3. 为当前问题先写失败测试。
4. 保留现有工作区改动，不做大规模格式化。

### 阶段 1：修复 P1

1. 播放器初始化异常边界。
2. session-aware 音频写入。
3. 原子配置保存。
4. 热键整批事务。

完成后应能发布内部测试版，不再新增功能。

### 阶段 2：资源与稳定性

1. 剪贴板单 STA 队列和 latest-only 语义。
2. 删除强制 GC/EmptyWorkingSet。
3. 流式 MP3 解码。
4. Edge 空闲超时和消息大小上限。
5. 阅读参数快照。

### 阶段 3：兼容性与体验

1. 多显示器坐标修复。
2. Unicode 截断和折行。
3. 卸载 LocalAppData 清理。
4. Mutex 所有权修复。
5. 删除或实现 EdgeProxy。

### 阶段 4：发布验收

1. 构建、单测、格式检查。
2. 真实 Edge/Azure 手动冒烟。
3. 无音频设备、断网、服务端挂起测试。
4. 100%/125%/150%/200% DPI。
5. 多显示器与负坐标。
6. 安装、覆盖升级、卸载、便携版。
7. 1 小时 soak test。

---

## 8. 发布验收清单

### 功能

- 复制即读。
- 新文本立即抢占。
- 暂停/继续。
- 停止后保留文本与缓存。
- 行点击 seek。
- 已合成句秒开。
- Edge 与 Azure 切换符合“下一次朗读生效”。
- 热键保存、取消、失败回滚。
- 朗读板显示/隐藏、拖动、缩放、尺寸持久化。
- 开机自启与卸载行为。

### 稳定性

- 连续 1 小时复制、暂停、继续、seek、清理，状态不卡死。
- 线程数不持续增长。
- 句柄数不持续增长。
- 快速 seek 后位置正确。
- 队列清空后回到 Idle/Stopped，不残留后台任务。

### 性能

记录并对比 v1.0.1：

| 指标 | 空闲 | 3150 字 | 5000 字 |
|---|---:|---:|---:|
| Private Bytes |  |  |  |
| Managed Heap |  |  |  |
| LOH |  |  |  |
| 线程数 |  |  |  |
| 句柄数 |  |  |  |
| 首声延迟 |  |  |  |
| 峰值内存 |  |  |  |

验收目标应由实际优化结果确定，不应通过强制工作集裁剪伪造。

### 安装

- 全新安装。
- 覆盖升级保留数据。
- 卸载默认保留数据。
- 用户确认删除时同时处理 `{app}\data` 和 `%LOCALAPPDATA%\SelectionTts`。
- 自启动快捷方式与应用设置状态一致。

---

## 9. 必须保留的设计

以下设计是当前产品的核心价值，不应在优化中破坏：

1. 只读剪贴板，不注入进程、不抓选区、不读屏幕。
2. 事件驱动监听，不轮询剪贴板。
3. 句级缓存和句首 seek。
4. 双代际隔离思路。
5. 单路播放和连续 PCM 缓冲。
6. 不丢音频的背压策略。
7. 不抢焦点的朗读板。
8. 150 ms UI 轮询，不制造高频状态事件。
9. DPAPI 保存 Azure Key。
10. 日志不记录剪贴板正文和密钥。
11. 便携数据目录优先、LocalAppData 回退。
12. 极少依赖、可解释的部署方式。

---

## 10. 给 OpenCode 的交付要求

OpenCode 完成修改后，应提交一份交付说明，至少包含：

1. 修改了哪些文件，为什么。
2. 每个 P1/P2 问题的修复方式。
3. 新增了哪些测试。
4. `dotnet build` 和测试命令及结果。
5. 是否做了性能对比，使用什么指标。
6. 未能修复的问题和原因。
7. 是否需要配置迁移。
8. 发布产物路径和体积。

代码修改要求：

- 小步提交，每阶段一个可审查结果。
- 不把报告中的全部问题混在一次大提交中。
- 不自行降低产品功能来规避问题。
- 不通过删除功能让测试变绿。
- 不新增与目标无关的依赖。
- 任何并发修复都要说明原先的交错路径以及修复后的同步边界。

最终目标不是把行数变少，而是让 v1.0.2 在以下四点上明显优于 v1.0.1：

- 更稳定：不卡死、不串音、不串会话。
- 更可信：保存失败会明确告知，配置和运行状态一致。
- 更轻量：没有伪内存优化，峰值内存和线程创建下降。
- 更可维护：关键路径有自动测试，后续修改不靠人工撞运气。
