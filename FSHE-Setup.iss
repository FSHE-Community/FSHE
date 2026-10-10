; FSHE 安装脚本 — Inno Setup 6.7
; 在仓库根目录下用 ISCC 编译本脚本，源文件取自 installer_src\

#define MyAppName "FSHE"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "YG-0"
#define MyAppExeName "FSHE.exe"
#define MyAppSourceDir "installer_src"

[Setup]
AppId={{8F4C3D2A-1B5E-4D8F-A6C1-9D0E7F1A2B3C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/YG-0/FSHE
AppSupportURL=https://github.com/YG-0/FSHE/issues
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=installer_output
OutputBaseFilename=FSHE-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: unchecked

[Files]
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "现在启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
