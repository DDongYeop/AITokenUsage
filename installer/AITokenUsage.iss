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
; AppMutex를 두지 않는다: 시작할 때 "실행 중이니 닫으라"는 창을 띄우는 대신 [Code]의 CloseRunningApp이 앱을 직접 닫는다.
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
english.DeleteUserData=Also delete settings and cache?%n%nThis removes the app's own data (including the Copilot sign-in it stored and the sign-ins of added accounts). AI CLI sign-in files are never touched.
english.AppStillRunning=AI Usage could not be closed. Exit it from the tray icon menu, then try again.
#if HasKorean
korean.AppDisplayName=AI 사용량
korean.StartWithWindows=Windows 시작 시 자동 실행
korean.OtherOptions=기타 옵션:
korean.LaunchApp=AI 사용량 실행
korean.DeleteUserData=설정과 캐시도 삭제할까요?%n%n앱이 저장한 데이터(앱에 저장된 Copilot 로그인과 추가한 계정 로그인 포함)를 지웁니다. AI CLI 로그인 파일은 건드리지 않습니다.
korean.AppStillRunning=AI 사용량을 닫지 못했습니다. 트레이 아이콘 메뉴에서 종료한 뒤 다시 시도하세요.
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "{cm:StartWithWindows}"; GroupDescription: "{cm:OtherOptions}"; Flags: unchecked

[Files]
; [InstallDelete]를 두지 않는다: 사용자가 고른 비어 있지 않은 폴더가 지워질 수 있고, self-contained 앱은 이전 버전의 잔여 파일이 있어도 문제가 없다.
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
; MIT 라이선스 조건: 배포본에 저작권 표시와 라이선스 전문을 함께 넣는다.
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{cm:AppDisplayName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{cm:AppDisplayName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AITokenUsage"; ValueData: """{app}\{#AppExe}"""; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
const
  AppMutexName = 'Local\AITokenUsage.SingleInstance';
  { SingleInstance.ExitEventName: 1.3.0부터 실행 중인 앱이 이 이벤트를 받으면 트레이의 [종료]와 같은 경로로 끝난다. }
  ExitEventName = 'Local\AITokenUsage.Exit';
  EVENT_MODIFY_STATE = $0002;

function OpenEvent(DesiredAccess: DWORD; InheritHandle: BOOL; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Event: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

{ 앱의 단일 실행 뮤텍스가 사라질 때까지 최대 TimeoutMs 동안 기다린다. }
function WaitForAppExit(TimeoutMs: Integer): Boolean;
var
  Waited: Integer;
begin
  Waited := 0;
  while CheckForMutexes(AppMutexName) and (Waited < TimeoutMs) do
  begin
    Sleep(200);
    Waited := Waited + 200;
  end;
  Result := not CheckForMutexes(AppMutexName);
end;

{ 실행 중인 앱을 닫는다. 종료 이벤트를 아는 버전은 스스로 설정을 저장하고 끝나고, 이벤트가 없는 이전 버전(1.2.0 이하)이나
  응답하지 않는 앱은 강제로 끝낸다. 앱은 settings.json과 cache.json을 임시 파일 교체로 저장하므로 강제 종료에도 파일이 깨지지 않는다. }
function CloseRunningApp(): Boolean;
var
  Event: THandle;
  ResultCode: Integer;
begin
  if not CheckForMutexes(AppMutexName) then
  begin
    Result := True;
    Exit;
  end;

  Event := OpenEvent(EVENT_MODIFY_STATE, False, ExitEventName);
  if Event <> 0 then
  begin
    SetEvent(Event);
    CloseHandle(Event);
    if WaitForAppExit(15000) then
    begin
      Result := True;
      Exit;
    end;
  end;

  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := WaitForAppExit(5000);
end;

{ [설치]를 누른 뒤에 닫는다: 마법사를 취소하면 앱은 그대로 실행 중이다. 끝 화면의 "실행" 항목이 앱을 다시 켠다. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if CloseRunningApp() then
    Result := ''
  else
    Result := CustomMessage('AppStillRunning');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { 제거를 확인한 뒤, 파일을 지우기 전에 닫는다. 닫지 못하면 사용 중인 파일만 남고 제거는 계속된다. }
  if (CurUninstallStep = usUninstall) and (not CloseRunningApp()) then
    SuppressibleMsgBox(CustomMessage('AppStillRunning'), mbError, MB_OK, IDOK);

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
