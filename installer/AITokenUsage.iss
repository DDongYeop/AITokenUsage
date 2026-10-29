; AI 사용량 위젯 설치 스크립트 (v1.1 설계 §5.2). tools/Build-Installer.ps1이 /DAppVersion=...으로 컴파일한다.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppExe "AITokenUsage.exe"
#define HasKorean FileExists(CompilerPath + "Languages\Korean.isl")

[Setup]
AppId={{135FF402-48F6-486E-B730-03060A8E9414}
AppName=AI Usage
AppVersion={#AppVersion}
AppVerName=AI Usage {#AppVersion}
AppPublisher=DDongYeop
AppCopyright=Copyright (C) 2026 DDongYeop
AppPublisherURL=https://github.com/DDongYeop/AITokenUsage
DefaultDirName={localappdata}\Programs\AITokenUsage
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
; 업그레이드 때 첫 설치의 작업 선택을 다시 적용하지 않는다: 안 고른 작업은 기존 Run 값과 바탕 화면 아이콘을 건드리지 않는다.
UsePreviousTasks=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\artifacts\installer
OutputBaseFilename=AITokenUsage-Setup-{#AppVersion}
SetupIconFile=..\src\AITokenUsage\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=AI Usage
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
AppMutex=Local\AITokenUsage.SingleInstance
CloseApplications=yes
RestartApplications=no
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=auto

[Languages]
#if HasKorean
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.AppDisplayName=AI Usage
english.StartWithWindows=Start with Windows
english.OtherOptions=Other options:
english.LaunchApp=Launch AI Usage
english.DeleteUserData=Also delete settings and cache?%n%nThis removes the app's own data (including the Copilot sign-in it stored). AI CLI sign-in files are never touched.
#if HasKorean
korean.AppDisplayName=AI 사용량
korean.StartWithWindows=Windows 시작 시 자동 실행
korean.OtherOptions=기타 옵션:
korean.LaunchApp=AI 사용량 실행
korean.DeleteUserData=설정과 캐시도 삭제할까요?%n%n앱이 저장한 데이터(앱에 저장된 Copilot 로그인 포함)를 지웁니다. AI CLI 로그인 파일은 건드리지 않습니다.
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "{cm:StartWithWindows}"; GroupDescription: "{cm:OtherOptions}"; Flags: unchecked

[Files]
; [InstallDelete]를 두지 않는다: 사용자가 고른 비어 있지 않은 폴더가 지워질 수 있고, self-contained 앱은 이전 버전의 잔여 파일이 있어도 문제가 없다.
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{cm:AppDisplayName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{cm:AppDisplayName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AITokenUsage"; ValueData: """{app}\{#AppExe}"""; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AITokenUsage');
    if (not UninstallSilent) and
       (MsgBox(CustomMessage('DeleteUserData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    begin
      DelTree(ExpandConstant('{userappdata}\AITokenUsage'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\AITokenUsage'), True, True, True);
    end;
  end;
end;
