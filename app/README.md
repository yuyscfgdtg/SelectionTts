# SelectionTts（C# 正式版）

便携式划词朗读工具。目标机器不需要安装 .NET，解压即用。

## 开发

```powershell
cd D:\桌面\SelectionTts_beta\SelectionTts_ProjectCatalog\ontology_file\app\SelectionTts
dotnet build -c Debug
dotnet run
```

## 发布便携版（单文件、自包含）

```powershell
cd D:\桌面\SelectionTts_beta\SelectionTts_ProjectCatalog\ontology_file
dotnet publish app\SelectionTts\SelectionTts.csproj -c Release -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish\win-x64
```

产物 `publish\win-x64\SelectionTts.exe` 可直接拷走运行；配置、日志、缓存会生成在 exe 旁边的 `data` 目录（目录不可写时回退到 `%LOCALAPPDATA%\SelectionTts`）。

## 数据目录

```
data\
  config.json
  logs\app.log
  cache\
```

## 图标

`app.ico` 是当前占位图标，替换同名文件后重新 build 即可。
