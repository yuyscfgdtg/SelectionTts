; 剪贴板朗读（SelectionTts）安装包脚本
; 用 Inno Setup 6 编译： "C:\Users\Administrator\AppData\Local\Programs\Inno Setup 6\ISCC.exe" SelectionTts.iss

#define MyAppName "剪贴板朗读"
#define MyAppVersion "1.0.2"
#define MyAppExeName "SelectionTts.exe"
#define MyAppId "{{6E1B2A94-3C7D-4F58-9A21-5D8B0C4E7F13}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppName}
DefaultDirName={autopf}\SelectionTts
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=output
OutputBaseFilename=SelectionTts-Setup-{#MyAppVersion}
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "autostart"; Description: "开机自动启动（托盘常驻）"; GroupDescription: "附加选项："
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项："; Flags: unchecked

[Files]
Source: "..\publish\win-x64\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
  LocalDir: string;
  Prompt: string;
begin
  { 卸载时默认保留数据（设置和日志），只有用户明确选择才删。
    数据可能在安装目录（便携优先），也可能回退到 LocalAppData，两处都要处理 }
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{app}\data');
    LocalDir := ExpandConstant('{localappdata}\SelectionTts');

    if (not DirExists(DataDir)) and (not DirExists(LocalDir)) then
      exit;

    if UninstallSilent then
      exit;

    Prompt := '是否同时删除设置和日志？' + #13#10;
    if DirExists(DataDir) then
      Prompt := Prompt + '（' + DataDir + '）' + #13#10;
    if DirExists(LocalDir) then
      Prompt := Prompt + '（' + LocalDir + '）';

    if MsgBox(Prompt, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      if DirExists(DataDir) then
        DelTree(DataDir, True, True, True);
      if DirExists(LocalDir) then
        DelTree(LocalDir, True, True, True);
    end;
  end;
end;
