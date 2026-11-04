; SPDX-License-Identifier: GPL-3.0-or-later
; Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club
;
; Admin-only installer: service (LocalSystem), WireGuard for Windows, protected data dir.
; Build with windows\installer\build.ps1 (it stages the files and passes StageDir/WgVpnVersion).

#ifndef StageDir
  #define StageDir "build\stage"
#endif
#ifndef WgVpnVersion
  #define WgVpnVersion "0.1.0"
#endif
#define ServiceName "WgVpnService"
#define AppExe "WgVpn.App.exe"
#define SvcExe "WgVpn.Service.exe"

[Setup]
AppId={{6F0B7D52-3C1E-4C57-9E1A-5B8D2A7C41E9}
AppName=ANCT WireGuard VPN
AppVersion={#WgVpnVersion}
AppPublisher=National Institute of Technology, Asahikawa College New-Tech-Club
DefaultDirName={autopf}\WgVpn
DisableDirPage=yes
DefaultGroupName=ANCT WireGuard VPN
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
LicenseFile={#StageDir}\LICENSE-GPL-3.0.txt
OutputDir=output
OutputBaseFilename=WgVpn-Setup-{#WgVpnVersion}
Compression=lzma2
SolidCompression=yes
CloseApplications=no
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
japanese.DataDirReparse=%1 がシンボリックリンク/ジャンクションです。安全のためインストールを中止します。フォルダーを確認して削除してから再実行してください。
english.DataDirReparse=%1 is a symbolic link or junction. Setup is aborting for safety. Inspect and remove it, then run Setup again.
japanese.DataDirOwner=%1 の所有者が SYSTEM / Administrators ではありません。安全のためインストールを中止します。フォルダーを確認して削除してから再実行してください。
english.DataDirOwner=%1 is not owned by SYSTEM or Administrators. Setup is aborting for safety. Inspect and remove it, then run Setup again.
japanese.DataDirFail=データフォルダー %1 を安全に準備できませんでした (%2)。
japanese.DataDirContents=%1 にリンク、所有者が SYSTEM / Administrators ではない項目、または独自のアクセス許可を持つ項目があります (または作成時にフォルダーが空ではありませんでした)。安全のためインストールを中止します。確認して削除してから再実行してください。
english.DataDirContents=%1 contains items that are links, are not owned by SYSTEM or Administrators, or carry their own permissions (or the folder was not empty when it was created). Setup is aborting for safety. Inspect and remove them, then run Setup again.
english.DataDirFail=Could not prepare the data folder %1 safely (%2).
japanese.WgInstallFail=WireGuard for Windows のインストールに失敗しました (終了コード %1)。
english.WgInstallFail=Installing WireGuard for Windows failed (exit code %1).
japanese.SvcFail=サービスの登録または起動に失敗しました (%1, 終了コード %2)。
english.SvcFail=Registering or starting the service failed (%1, exit code %2).
japanese.SvcRerun=インストーラをもう一度実行してください。
english.SvcRerun=Please run Setup again.
japanese.DataLeft=データフォルダー %1 を削除できませんでした。接続バンドル（秘密鍵）が残っている可能性があります。内容を確認して手動で削除してください。
english.DataLeft=Could not delete the data folder %1. The bundle (private key) may remain there. Inspect it and delete it manually.
japanese.SvcStopFail=既存のサービスを停止できませんでした。
english.SvcStopFail=Could not stop the existing service.
japanese.KeepWireGuard=WireGuard for Windows はアンインストールされません。不要なら Windows の設定から別途削除してください。
english.KeepWireGuard=WireGuard for Windows is not uninstalled. Remove it separately from Windows Settings if you no longer need it.

[Files]
Source: "{#StageDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\{#SvcExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\wstunnel.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\LICENSE-GPL-3.0.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\LICENSE-wstunnel.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\wireguard-amd64.msi"; Flags: dontcopy

[Icons]
Name: "{group}\ANCT WireGuard VPN"; Filename: "{app}\{#AppExe}"

[Registry]
; Start the tray GUI for every user at logon (runs non-elevated; the service does the privileged work).
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WgVpn"; ValueData: """{app}\{#AppExe}"" --tray"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,ANCT WireGuard VPN}"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: ShouldLaunchGui

[Code]
const
  SvcName = '{#ServiceName}';

var
  ServiceFailed: Boolean;

function DataDir: String;
begin
  Result := ExpandConstant('{commonappdata}\WgVpn');
end;

function RunTool(const FileName, Params: String): Integer;
var
  Code: Integer;
begin
  if Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := Code
  else
    Result := -1;
  Log(Format('%s %s -> %d', [FileName, Params, Result]));
end;

function Sc(const Params: String): Integer;
begin
  Result := RunTool(ExpandConstant('{sys}\sc.exe'), Params);
end;

function ServiceExists: Boolean;
begin
  Result := Sc('query ' + SvcName) = 0;
end;

function ServiceStopped: Boolean;
begin
  { sc query fails (1060) when the service does not exist; otherwise look for STATE : 1 (STOPPED). }
  if not ServiceExists then
    Result := True
  else
    Result := RunTool(ExpandConstant('{cmd}'),
      '/c ""' + ExpandConstant('{sys}\sc.exe') + '" query ' + SvcName + ' | "' +
      ExpandConstant('{sys}\findstr.exe') + '" /R /C:"STATE *: 1 ""') = 0;
end;

{ Stop the service and wait: stopping tears the tunnel down, which can take a while. }
function StopServiceAndWait: Boolean;
var
  I: Integer;
begin
  if ServiceExists then
  begin
    Sc('stop ' + SvcName);
    for I := 1 to 200 do
    begin
      if ServiceStopped then Break;
      Sleep(1000);
    end;
  end;
  Result := ServiceStopped;
end;

procedure CloseGui;
begin
  RunTool(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}');
end;

{ One PowerShell script does the whole data-folder job so that a new folder is created with a protected DACL
  in the same call (never an open one) and an existing folder is checked right before it is changed.
  Exit codes: 0 ok, 2 reparse point, 3 owner not SYSTEM/Administrators, 4 folder appeared/has suspicious
  children, 5 other failure, 6 verification failed. With -CheckOnly nothing is changed. }
function DataDirScript: String;
begin
  Result :=
    'param([string]$p, [switch]$CheckOnly)' + #13#10 +
    '$ErrorActionPreference = ''Stop''' + #13#10 +
    'try {' + #13#10 +
    '  $system = New-Object Security.Principal.SecurityIdentifier(''S-1-5-18'')' + #13#10 +
    '  $admins = New-Object Security.Principal.SecurityIdentifier(''S-1-5-32-544'')' + #13#10 +
    '  function New-Sec {' + #13#10 +
    '    $s = New-Object Security.AccessControl.DirectorySecurity' + #13#10 +
    '    $s.SetOwner($admins)' + #13#10 +
    '    $s.SetAccessRuleProtection($true, $false)' + #13#10 +
    '    foreach ($sid in $system, $admins) {' + #13#10 +
    '      $s.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, ''FullControl'', ''ContainerInherit,ObjectInherit'', ''None'', ''Allow'')))' + #13#10 +
    '    }' + #13#10 +
    '    return $s' + #13#10 +
    '  }' + #13#10 +
    '  function Test-Trusted([string]$path) {' + #13#10 +
    '    $owner = (Get-Acl -LiteralPath $path).GetOwner([Security.Principal.SecurityIdentifier])' + #13#10 +
    '    return ($owner -eq $system -or $owner -eq $admins)' + #13#10 +
    '  }' + #13#10 +
    '  function Test-Folder {' + #13#10 +
    '    $i = Get-Item -Force -LiteralPath $p' + #13#10 +
    '    if ($i.Attributes -band [IO.FileAttributes]::ReparsePoint) { return 2 }' + #13#10 +
    '    if (-not (Test-Trusted $p)) { return 3 }' + #13#10 +
    '    return 0' + #13#10 +
    '  }' + #13#10 +
    '  function Test-Children {' + #13#10 +
    '    foreach ($c in Get-ChildItem -Force -LiteralPath $p) {' + #13#10 +
    '      if ($c.Attributes -band [IO.FileAttributes]::ReparsePoint) { return 4 }' + #13#10 +
    '      if (-not (Test-Trusted $c.FullName)) { return 4 }' + #13#10 +
    '      # After the DACL lock every legitimate child inherits. An explicit ACE (e.g. a planted Users grant) is not ours.' + #13#10 +
    '      foreach ($rule in (Get-Acl -LiteralPath $c.FullName).GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier])) { return 4 }' + #13#10 +
    '    }' + #13#10 +
    '    return 0' + #13#10 +
    '  }' + #13#10 +
    '  $attr = $null' + #13#10 +
    '  try { $attr = [IO.File]::GetAttributes($p) } catch [IO.FileNotFoundException] {} catch [IO.DirectoryNotFoundException] {}' + #13#10 +
    '  if ($null -eq $attr) {' + #13#10 +
    '    if ($CheckOnly) { exit 0 }' + #13#10 +
    '    (New-Object IO.DirectoryInfo($p)).Create((New-Sec))' + #13#10 +
    '    # CreateDirectory succeeds on an existing folder, so prove it is ours: trusted owner, empty, not a link.' + #13#10 +
    '    $r = Test-Folder; if ($r -ne 0) { exit $r }' + #13#10 +
    '    if (@(Get-ChildItem -Force -LiteralPath $p).Count -ne 0) { exit 4 }' + #13#10 +
    '  } else {' + #13#10 +
    '    $r = Test-Folder; if ($r -ne 0) { exit $r }' + #13#10 +
    '    if ($CheckOnly) { exit 0 }' + #13#10 +
    '    (New-Object IO.DirectoryInfo($p)).SetAccessControl((New-Sec))' + #13#10 +
    '    # Scan only after the lock: nobody else can add or re-ACL children from here on, so one pass is final.' + #13#10 +
    '    if ((Test-Children) -ne 0) { exit 4 }' + #13#10 +
    '  }' + #13#10 +
    '  if ($CheckOnly) { exit 0 }' + #13#10 +
    '  # Verify: still a real folder, trusted owner, protected DACL with exactly SYSTEM + Administrators.' + #13#10 +
    '  $r = Test-Folder; if ($r -ne 0) { exit $r }' + #13#10 +
    '  $acl = Get-Acl -LiteralPath $p' + #13#10 +
    '  $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))' + #13#10 +
    '  if (-not $acl.AreAccessRulesProtected -or $rules.Count -ne 2) { exit 6 }' + #13#10 +
    '  foreach ($rule in $rules) {' + #13#10 +
    '    if ($rule.IdentityReference -ne $system -and $rule.IdentityReference -ne $admins) { exit 6 }' + #13#10 +
    '    if ($rule.AccessControlType -ne ''Allow'' -or $rule.FileSystemRights -ne ''FullControl'') { exit 6 }' + #13#10 +
    '  }' + #13#10 +
    '  exit 0' + #13#10 +
    '} catch { exit 5 }' + #13#10;
end;

function RunDataDirScript(const CheckOnly: Boolean): Integer;
var
  Extra: String;
begin
  SaveStringToFile(ExpandConstant('{tmp}\wgvpn-datadir.ps1'), DataDirScript, False);
  Extra := '';
  if CheckOnly then Extra := ' -CheckOnly';
  Result := RunTool(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\wgvpn-datadir.ps1') +
    '" -p "' + DataDir + '"' + Extra);
end;

{ Returns '' on success, else the abort message. }
function PrepareDataDir: String;
var
  R: Integer;
begin
  R := RunDataDirScript(False);
  case R of
    0: Result := '';
    2: Result := FmtMessage(CustomMessage('DataDirReparse'), [DataDir]);
    3: Result := FmtMessage(CustomMessage('DataDirOwner'), [DataDir]);
    4: Result := FmtMessage(CustomMessage('DataDirContents'), [DataDir]);
  else
    Result := FmtMessage(CustomMessage('DataDirFail'), [DataDir, 'exit ' + IntToStr(R)]);
  end;
end;

function InstallWireGuardIfMissing: String;
var
  R: Integer;
begin
  if FileExists(ExpandConstant('{commonpf}\WireGuard\wireguard.exe')) then
  begin
    Log('WireGuard for Windows already installed.');
    Exit;
  end;
  ExtractTemporaryFile('wireguard-amd64.msi');
  R := RunTool(ExpandConstant('{sys}\msiexec.exe'),
    '/i "' + ExpandConstant('{tmp}\wireguard-amd64.msi') + '" DO_NOT_LAUNCH=1 /qn /norestart');
  if (R <> 0) and (R <> 3010) then
    Result := FmtMessage(CustomMessage('WgInstallFail'), [IntToStr(R)]);
end;

{ Everything that can fail runs here, before any file is copied; a non-empty result aborts cleanly. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  WasRunning: Boolean;
begin
  CloseGui;
  WasRunning := ServiceExists and not ServiceStopped;
  if not StopServiceAndWait then
  begin
    Result := CustomMessage('SvcStopFail');
    Exit;
  end;
  Result := PrepareDataDir;
  if Result = '' then Result := InstallWireGuardIfMissing;
  { Upgrade aborted: leave the user with the service they had. }
  if (Result <> '') and WasRunning then Sc('start ' + SvcName);
end;

function ServiceRunning: Boolean;
begin
  Result := RunTool(ExpandConstant('{cmd}'),
    '/c ""' + ExpandConstant('{sys}\sc.exe') + '" query ' + SvcName + ' | "' +
    ExpandConstant('{sys}\findstr.exe') + '" /R /C:"STATE *: 4 ""') = 0;
end;

procedure FailService(const What: String; Code: Integer);
begin
  ServiceFailed := True;
  SuppressibleMsgBox(FmtMessage(CustomMessage('SvcFail'), [What, IntToStr(Code)]) + #13#10#13#10 +
    CustomMessage('SvcRerun'), mbError, MB_OK, IDOK);
end;

{ Never raises: RaiseException here is not relied on to abort Setup. Failure sets ServiceFailed instead. }
procedure RegisterAndStartService;
var
  BinPath, Common: String;
  R, I: Integer;
begin
  { Escaped quotes keep the path quoted for sc (the path contains spaces). }
  BinPath := '\"' + ExpandConstant('{app}\{#SvcExe}') + '\"';
  Common := 'binPath= "' + BinPath + '" start= auto obj= LocalSystem DisplayName= "ANCT WireGuard VPN Service"';
  if ServiceExists then
    R := Sc('config ' + SvcName + ' ' + Common)
  else
    R := Sc('create ' + SvcName + ' ' + Common);
  if R <> 0 then begin FailService('create/config', R); Exit; end;

  R := Sc('failure ' + SvcName + ' reset= 86400 actions= restart/5000/restart/5000/restart/5000');
  if R <> 0 then begin FailService('failure', R); Exit; end;

  Sc('description ' + SvcName + ' "Runs wstunnel and WireGuard for ANCT WireGuard VPN (LocalSystem)."');

  R := Sc('start ' + SvcName);
  if (R <> 0) and (R <> 1056) then begin FailService('start', R); Exit; end;

  { sc start returns at START_PENDING: wait (bounded) until the service really runs. }
  for I := 1 to 60 do
  begin
    if ServiceRunning then Exit;
    Sleep(1000);
  end;
  FailService('not running after start', 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then RegisterAndStartService;
end;

function GetCustomSetupExitCode: Integer;
begin
  if ServiceFailed then Result := 1 else Result := 0;
end;

function ShouldLaunchGui: Boolean;
begin
  Result := not ServiceFailed;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  R: Integer;
  Left: Boolean;
begin
  if CurUninstallStep = usUninstall then
  begin
    CloseGui;
    { Stopping the service tears the tunnel down (routes, WireGuard tunnel service, wstunnel). }
    StopServiceAndWait;
    if ServiceExists then
    begin
      R := Sc('delete ' + SvcName);
      if R <> 0 then Log('sc delete failed: ' + IntToStr(R));
    end;
    { Best effort: a tunnel service left behind by a failed teardown. }
    if Sc('query WireGuardTunnel$wgvpn') = 0 then
      RunTool(ExpandConstant('{commonpf}\WireGuard\wireguard.exe'), '/uninstalltunnelservice wgvpn');
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    { Holds bundle.json (contains the WireGuard private key). Never follow a link planted here. }
    Left := True;
    R := RunDataDirScript(True);
    if R = 0 then
    begin
      DelTree(DataDir, True, True, True);
      Left := DirExists(DataDir);
      if Left then Log('Could not delete the data folder: ' + DataDir);
    end
    else
      Log('Data folder failed the safety check (exit ' + IntToStr(R) + '), left in place: ' + DataDir);
    if Left then
      SuppressibleMsgBox(FmtMessage(CustomMessage('DataLeft'), [DataDir]), mbError, MB_OK, IDOK)
    else
      Log('Deleted ' + DataDir);
    if not UninstallSilent then
      MsgBox(CustomMessage('KeepWireGuard'), mbInformation, MB_OK);
  end;
end;
