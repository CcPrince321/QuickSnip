; QuickSnip 安装包脚本
; 编译：ISCC.exe installer\QuickSnip.iss
; 前置：先完成自包含发布  dotnet publish src\QuickSnip.csproj -c Release -r win-x64 ...
;        产物在 dist\QuickSnip.exe

#define MyAppName "QuickSnip"
#define MyAppVersion "0.2.0"
#define MyAppPublisher "CcPrince321"
#define MyAppExeName "QuickSnip.exe"

[Setup]
AppId={{6E1C42D0-8F1A-4C39-9B4C-1D71AA0F2C7E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={userpf}\{#MyAppName}
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=QuickSnip-Setup-v{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
; 正在运行时检测到我们的单实例 mutex，会提示用户先退出
AppMutex=QuickSnip.SingleInstance
LicenseFile=..\LICENSE

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; \
  GroupDescription: "附加任务:"
Name: "autostart"; Description: "开机自动启动（截图工具需要常驻才能响应热键）(&A)"; \
  GroupDescription: "附加任务:"

[Files]
Source: "..\dist\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "{#MyAppName}"; \
  ValueData: """{app}\{#MyAppExeName}"""; \
  Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; \
  Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载只删程序，不动 %APPDATA%\QuickSnip 里的配置和截图

[Code]
var
  SaveDirPage: TInputDirWizardPage;

procedure InitializeWizard;
begin
  // 截图保存位置：放在「安装位置」之后，独立一页，用户必须做出选择
  SaveDirPage := CreateInputDirPage(wpSelectDir,
    '选择截图保存位置',
    '截图会自动保存为 PNG 到这个文件夹。',
    '选择截图保存文件夹，然后点“下一步”。以后想改，编辑 %APPDATA%\QuickSnip\settings.json 的 saveDirectory 字段即可。',
    False, '');
  SaveDirPage.Add('截图保存文件夹:');
  SaveDirPage.Values[0] := ExpandConstant('{userpics}\QuickSnip');
end;

// 安装完成后，把用户选的目录写给程序：
// 写 marker 文件，程序首次启动时采纳并删除（见 App.ApplyInstallerSaveDirectoryHint）。
procedure CurStepChanged(CurStep: TSetupStep);
var
  MarkerPath: string;
begin
  if CurStep = ssPostInstall then
  begin
    MarkerPath := ExpandConstant('{userappdata}\QuickSnip\save-directory.txt');
    ForceDirectories(ExtractFileDir(MarkerPath));
    SaveStringToFile(MarkerPath, SaveDirPage.Values[0], False);
  end;
end;
