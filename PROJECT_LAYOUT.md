# 项目目录结构

本仓库（`yuyscfgdtg/SelectionTts`）**只承载软件本体**：源码、构建脚本、历史报告。
完整工程在本地采用五区结构，其余四区属于"过程与记忆"，**不入库**。

```
SelectionTts_ProjectCatalog\
├─ ontology_file\            ← 软件本体（= 本仓库）
│   ├─ app\SelectionTts\     WPF 工程（.NET 10）
│   ├─ docs\                 历史报告与截图
│   └─ README.md / LICENSE / .github\
├─ Official_version\         ← 已发布的正式版本快照（vX.X.X）
├─ Beta_Version_Archive\     ← 未发布版本存档（编号 YYYYMMDD_NNNN，用于逐步回滚）
├─ Memory_Archives\          ← 项目记忆：开发规则、会话记录、当前状态
└─ Update_records\           ← 变更记录：CHANGELOG、单次改动明细、机读索引
```

## 各区的职责

| 区 | 内容 | 是否入库 |
|---|---|---|
| `ontology_file\` | 源码、构建配置、安装脚本、历史报告 | **是**（本仓库） |
| `Official_version\` | 每个已发布版本的全量源码快照 + 同名 zip | 否 |
| `Beta_Version_Archive\` | 每一步开发的中间存档，命名 `20261117_0001` | 否 |
| `Memory_Archives\` | 开发规则、会话存档、当前状态摘要 | 否 |
| `Update_records\` | `CHANGELOG.md`、单次改动明细、`index.json` | 否 |

## 版本归档规则

- **正式版本**：`Official_version\v1.0.3\`（源码全量）+ `Official_version\v1.0.3.zip`
  - 对应 Git tag `v1.0.3`，发布产物在 [Releases](https://github.com/yuyscfgdtg/SelectionTts/releases)
- **未发布存档**：`Beta_Version_Archive\20261117_0001\`
  - 日期 + 当天流水号，每天从 `0001` 归零；用于按步回滚
  - 某个存档正式发布后，会在 `Official_version\` 下另存一份 `vX.X.X`

## 为什么不把快照和记忆一起入库

- 任意历史版本都能用 `git checkout <tag>` 精确还原，`Official_version\` 与 tag 内容重复
- `Memory_Archives\` / `Update_records\` 每个会话都在改，入库会让 diff 噪音盖过代码变更，`git log` 也不再"一次提交 = 一次代码改动"

## 构建与发布

```powershell
# 构建（开发）
cd app\SelectionTts
dotnet build -c Debug

# 发布单文件（自包含，目标机器无需 .NET）
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish\win-x64

# 安装包（Inno Setup 6）
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\SelectionTts.iss
```

- 版本号写在 `app\SelectionTts\SelectionTts.csproj` 与 `app\SelectionTts\installer\SelectionTts.iss`，两处要一致
- 发布版绑定的提交由 `ProductVersion`（`x.y.z+<commit>`）体现，因此**先提交再发布**

## 相关

- 使用说明：[README.md](README.md)
- 问题反馈：[Issues](https://github.com/yuyscfgdtg/SelectionTts/issues)
- 版本下载：[Releases](https://github.com/yuyscfgdtg/SelectionTts/releases)
