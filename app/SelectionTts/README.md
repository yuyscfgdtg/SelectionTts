# 剪贴板朗读（SelectionTts）

Windows 10/11 托盘小工具：**复制文本，自动朗读**。  
用 Edge 在线语音（和 Edge「大声朗读」同一个引擎），无需登录、无需 API Key。

> 早期版本的「划词/选区朗读」与「选区弹窗」已经移除，现在只监听系统剪贴板。

## 使用

1. 运行 `SelectionTts.exe`，托盘出现图标。
2. 按 `Ctrl+Alt+R` 开启监听（再按一次关闭），托盘提示会显示「监听中 / 未监听」。
   也可以右键托盘图标 →「监听剪贴板」。**默认是关闭的**，且开关状态会被记住。
3. 在任意程序里复制文本（`Ctrl+C` 或右键复制），程序会自动朗读。
4. 朗读过程中再复制新内容，会立即停掉旧的、朗读新的。
5. 关闭监听时会同时停止正在播放的朗读。

## 监听规则

| 情况 | 行为 |
|---|---|
| 普通文本 | 朗读 |
| 图片、文件列表（资源管理器里复制文件） | 忽略 |
| 密码管理器等声明「不要被剪贴板工具处理」的内容 | 忽略 |
| 同一段文本 3 秒内重复复制 | 忽略（防手抖）；超过 3 秒会重新朗读 |
| 连续快速复制多段 | 只朗读最后一次 |
| 超过 5000 字 | 只朗读前 5000 字 |

不会修改、还原你的剪贴板内容，也不弹窗、不抢焦点。

## 托盘菜单

- 监听剪贴板（等同于 `Ctrl+Alt+R`）
- 停止朗读
- 音色 / 语速
- 打开数据目录 / 打开日志
- 退出

## 数据目录

优先使用 exe 旁边的 `data` 目录（便携）；如果不可写，则退到 `%LOCALAPPDATA%\SelectionTts`。

```
data\
  config.json     设置
  logs\app.log    运行日志（排查问题看这里）
  cache\          自检缓存
```

`config.json` 字段：

| 字段 | 说明 |
|---|---|
| `voice` | Edge 音色，例如 `zh-CN-XiaoxiaoNeural` |
| `rate` | 语速，`-20%` / `+0%` / `+20%` / `+50%` |
| `maxChars` | 单次朗读的最大字数，默认 5000 |
| `engine` | 预留，目前固定 `edge` |
| `edgeProxy` | 预留的代理设置 |
| `watchEnabled` | 上次的监听开关状态（默认 `false`） |

## 命令行参数（调试用）

| 参数 | 作用 |
|---|---|
| `--watch` | 启动时强制开启监听（不写入配置） |
| `--selftest-drm` | 输出 Edge TTS 令牌，用于和 Python 版比对 |
| `--selftest-tts` | 合成并播放一段测试文本 |

## 构建

需要 .NET SDK 10。

```powershell
cd app\SelectionTts
dotnet build -c Debug
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -o publish\win-x64
```

发布产物是单文件，目标机器不需要安装 .NET。

## 已知限制

- 依赖网络：Edge TTS 是在线服务，断网时无法朗读。
- Edge 的接口令牌算法会随版本变化，升级时需同步 `Tts/DrmToken.cs` 里的版本号和常量。
- 长文本仍是「整段合成后再播放」，1400 字以上会有约 2 秒的合成等待。
