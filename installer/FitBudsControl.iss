#ifndef MyAppVersion
  #define MyAppVersion "1.0.48"
#endif

#define MyAppName "FitBuds Turbo 控制"
#define MyAppPublisher "FitBudsControl contributors"
#define MyAppExeName "FitBudsControl.exe"

[Setup]
AppId={{9EAE4D80-AB33-465D-89D0-A0D907BBAF71}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\FitBudsControl
DefaultGroupName=FitBuds Turbo
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
UsePreviousPrivileges=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=FitBudsControl-Setup-{#MyAppVersion}
SetupIconFile=..\src\FitBudsControl\Assets\AppIcon.ico
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Files]
Source: "..\artifacts\single-file\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\FitBuds Turbo 控制"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 FitBuds Turbo 控制"; Flags: nowait postinstall skipifsilent runasoriginaluser
