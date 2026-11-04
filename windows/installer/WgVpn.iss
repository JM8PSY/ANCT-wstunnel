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
DefaultGroupName=ANCT WireGuard VPN
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
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
english.DataDirFail=Could not prepare the data folder %1 safely (%2).
japanese.WgInstallFail=WireGuard for Windows のインストールに失敗しました (終了コード %1)。
english.WgInstallFail=Installing WireGuard for Windows failed (exit code %1).
japanese.SvcFail=サービスの登録または起動に失敗しました (%1, 終了コード %2)。
english.SvcFail=Registering or starting the service failed (%1, exit code %2).
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
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WgVpn"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,ANCT WireGuard VPN}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
const
  SvcName = '{#ServiceName}';
  SidSystem = '*S-1-5-18';
  SidAdmins = '*S-1-5-32-544';

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

{ 0 ok, 2 reparse point, 3 owner is neither SYSTEM nor Administrators, other = could not check. }
function CheckDataDir: Integer;
var
  Script: String;
begin
  Script :=
    'param([string]$p)' + #13#10 +
    'if (-not (Test-Path -LiteralPath $p)) { exit 0 }' + #13#10 +
    '$i = Get-Item -Force -LiteralPath $p' + #13#10 +
    'if ($i.Attributes -band [IO.FileAttributes]::ReparsePoint) { exit 2 }' + #13#10 +
    '$o = (Get-Acl -LiteralPath $p).GetOwner([Security.Principal.SecurityIdentifier]).Value' + #13#10 +
    'if ($o -ne ''S-1-5-18'' -and $o -ne ''S-1-5-32-544'') { exit 3 }' + #13#10 +
    'exit 0' + #13#10;
  SaveStringToFile(ExpandConstant('{tmp}\wgvpn-check.ps1'), Script, False);
  Result := RunTool(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\wgvpn-check.ps1') +
    '" -p "' + DataDir + '"');
end;

{ Returns '' on success, else the abort message. }
function PrepareDataDir: String;
var
  R: Integer;
begin
  R := CheckDataDir;
  if R = 2 then begin Result := FmtMessage(CustomMessage('DataDirReparse'), [DataDir]); Exit; end;
  if R = 3 then begin Result := FmtMessage(CustomMessage('DataDirOwner'), [DataDir]); Exit; end;
  if R <> 0 then begin Result := FmtMessage(CustomMessage('DataDirFail'), [DataDir, 'check ' + IntToStr(R)]); Exit; end;

  if not DirExists(DataDir) then
    if not CreateDir(DataDir) then
    begin
      Result := FmtMessage(CustomMessage('DataDirFail'), [DataDir, 'mkdir']);
      Exit;
    end;

  { Owner = Administrators; DACL protected, SYSTEM + Administrators only. SIDs work on any UI language. }
  R := RunTool(ExpandConstant('{sys}\icacls.exe'), '"' + DataDir + '" /setowner ' + SidAdmins);
  if R <> 0 then begin Result := FmtMessage(CustomMessage('DataDirFail'), [DataDir, 'setowner ' + IntToStr(R)]); Exit; end;
  R := RunTool(ExpandConstant('{sys}\icacls.exe'),
    '"' + DataDir + '" /inheritance:r /grant:r ' + SidSystem + ':(OI)(CI)F ' + SidAdmins + ':(OI)(CI)F');
  if R <> 0 then begin Result := FmtMessage(CustomMessage('DataDirFail'), [DataDir, 'icacls ' + IntToStr(R)]); Exit; end;

  { Re-check after the ACL change, in case the folder was swapped in between. }
  R := CheckDataDir;
  if R <> 0 then Result := FmtMessage(CustomMessage('DataDirFail'), [DataDir, 'recheck ' + IntToStr(R)]);
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
begin
  CloseGui;
  if not StopServiceAndWait then
  begin
    Result := CustomMessage('SvcStopFail');
    Exit;
  end;
  Result := PrepareDataDir;
  if Result <> '' then Exit;
  Result := InstallWireGuardIfMissing;
end;

procedure RegisterAndStartService;
var
  BinPath, Common: String;
  R: Integer;
begin
  { Escaped quotes keep the path quoted for sc (the path contains spaces). }
  BinPath := '\"' + ExpandConstant('{app}\{#SvcExe}') + '\"';
  Common := 'binPath= "' + BinPath + '" start= auto obj= LocalSystem DisplayName= "ANCT WireGuard VPN Service"';
  if ServiceExists then
    R := Sc('config ' + SvcName + ' ' + Common)
  else
    R := Sc('create ' + SvcName + ' ' + Common);
  if R <> 0 then RaiseException(FmtMessage(CustomMessage('SvcFail'), ['create/config', IntToStr(R)]));

  R := Sc('failure ' + SvcName + ' reset= 86400 actions= restart/5000/restart/5000/restart/5000');
  if R <> 0 then RaiseException(FmtMessage(CustomMessage('SvcFail'), ['failure', IntToStr(R)]));

  Sc('description ' + SvcName + ' "Runs wstunnel and WireGuard for ANCT WireGuard VPN (LocalSystem)."');

  R := Sc('start ' + SvcName);
  if (R <> 0) and (R <> 1056) then
  begin
    Sc('delete ' + SvcName);
    RaiseException(FmtMessage(CustomMessage('SvcFail'), ['start', IntToStr(R)]));
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then RegisterAndStartService;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  R: Integer;
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
    if CheckDataDir = 0 then
      DelTree(DataDir, True, True, True)
    else
      Log('Not deleting ' + DataDir + ': failed the reparse/owner check.');
    if not UninstallSilent then
      MsgBox(CustomMessage('KeepWireGuard'), mbInformation, MB_OK);
  end;
end;
