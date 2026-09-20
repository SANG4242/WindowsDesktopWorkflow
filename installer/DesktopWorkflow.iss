; DesktopWorkflow Inno Setup 配置文件
; 支持构建免 UAC 提权的用户级现代安装包，提供桌面快捷方式与开机启动选项

#ifndef MyAppVersion
#define MyAppVersion "1.0.0"
#endif

#define MyAppName "桌面工作流"
#define MyAppEnglishName "DesktopWorkflow"
#define MyAppPublisher "DesktopWorkflow Contributors"
#ifndef MyAppURL
#define MyAppURL "https://github.com/SANG4242/WindowsDesktopWorkflow"
#endif
#define MyAppExeName "DesktopWorkflow.exe"

#ifndef SourceDir
#define SourceDir "..\publish\app"
#endif

#ifndef OutputDir
#define OutputDir "..\publish\installer"
#endif

[Setup]
AppId={{9F5D2C8A-3E1B-4A7F-9C2E-8B6A4F1D7C3E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\{#MyAppEnglishName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=DesktopWorkflow-Setup-v{#MyAppVersion}-x64
SetupIconFile=..\src\DesktopWorkflow.App\Assets\desktop-workflow.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=Local\DesktopWorkflow-WPF,Local\DesktopWorkflow-Publish
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "随 Windows 登录自动启动托盘"; GroupDescription: "系统集成"

[Files]
; 主程序文件与全部运行库
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; 随包附带的独立多尺寸图标资源
Source: "..\src\DesktopWorkflow.App\Assets\desktop-workflow.ico"; DestDir: "{app}\icons"; Flags: ignoreversion
Source: "..\src\DesktopWorkflow.App\Assets\desktop-workflow.png"; DestDir: "{app}\icons"; Flags: ignoreversion

; 随包附带的公开示例工作流
Source: "..\examples\*"; DestDir: "{app}\examples"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 开始菜单快捷方式
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"

; 桌面快捷方式（根据用户选择）
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; 随 Windows 登录启动（根据用户选择）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DesktopWorkflow"; ValueData: """{app}\{#MyAppExeName}"" --startup"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; 安装完成后允许用户立即运行
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// 卸载提示是否保留用户的工作流配置数据
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\DesktopWorkflow');
    if DirExists(DataDir) then
    begin
      if MsgBox('是否同时删除您保存在本地的工作流配置文件与运行日志？' #13#10 #13#10 +
                '位置：' + DataDir #13#10 #13#10 +
                '提示：点击“否”可为您保留所有工作流配置，以便今后重新安装。',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
