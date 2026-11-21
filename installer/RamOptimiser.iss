; RAM Optimiser setup (Inno Setup 7.1.1). Built by site\scripts\package.ps1, which passes the three defines:
;   ISCC.exe /DAppVersion=yyyy.MM.dd /DBinDir=<build folder> /DRootDir=<repo> /O<output folder> RamOptimiser.iss
; The point of the installer is the protected folder: the logon task starts RamOptimiser.exe as administrator
; without a prompt, so the exe must sit where only administrators can change it. Hence an admin install into
; Program Files with no way to pick another folder. The setup is not code-signed yet, so SmartScreen shows
; "Unknown publisher".

#ifndef AppVersion
  #error Define AppVersion (yyyy.MM.dd), for example ISCC.exe /DAppVersion=2026.11.19
#endif
#ifndef BinDir
  #error Define BinDir, the folder build.ps1 wrote the exes to
#endif
#ifndef RootDir
  #error Define RootDir, the repository folder (README.md and LICENSE)
#endif

[Setup]
AppId={{5FD004AF-D02B-4275-B5CB-BD4FFA388823}
AppName=RAM Optimiser
AppVersion={#AppVersion}
AppVerName=RAM Optimiser {#AppVersion}
AppPublisher=strange-exe
AppPublisherURL=https://github.com/strange-exe/RamOptimiser
AppSupportURL=https://github.com/strange-exe/RamOptimiser/issues
AppUpdatesURL=https://github.com/strange-exe/RamOptimiser/releases/latest
VersionInfoVersion={#AppVersion}
UninstallDisplayName=RAM Optimiser
UninstallDisplayIcon={app}\RamOptimiser.exe
; Always elevated, and no /CURRENTUSER or per-user dialog: a per-user folder is exactly what the logon task must
; not start from.
PrivilegesRequired=admin
DefaultDirName={autopf}\RAM Optimiser
DisableDirPage=yes
; The folder is fixed, so an update lands in the same place without reading it back from the registry.
UsePreviousAppDir=no
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
SetupIconFile={#BinDir}\obj\RamOptimiser.ico
OutputDir={#BinDir}
OutputBaseFilename=RamOptimiser-{#AppVersion}-setup
SolidCompression=yes
; Restart Manager closes a running copy before its exe is replaced. It is not restarted from here: setup runs as
; administrator, and the finish page's tick box starts the app as the user instead.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#BinDir}\RamOptimiser.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\ramopt.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RootDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RootDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\RAM Optimiser"; Filename: "{app}\RamOptimiser.exe"
Name: "{autodesktop}\RAM Optimiser"; Filename: "{app}\RamOptimiser.exe"; Tasks: desktopicon

[Run]
; runasoriginaluser: the app starts with the rights of whoever started setup (normally not elevated), never as
; the administrator setup runs as. A silent install starts nothing.
Filename: "{app}\RamOptimiser.exe"; Description: "Start RAM Optimiser"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
; Removes the logon tasks that start this copy. Only tasks in the Task Scheduler root folder named RamOptimiser or
; RamOptimiser-<SID> whose single action runs exactly {app}\RamOptimiser.exe; a task for another copy is left alone.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "{code:TaskCleanupParams}"; Flags: runhidden; RunOnceId: "RemoveLogonTasks"

[Code]
// The PowerShell command for [UninstallRun], expanded when setup writes the uninstall log. It uses the Task
// Scheduler COM object rather than the ScheduledTasks module, so no module file is loaded and the execution
// policy does not apply. The script holds no double quote (only the two around -Command), and the path is put in
// single quotes with any single quote doubled, so nothing in it can end the string. $a is cleared on every pass and
// a task whose definition cannot be read is skipped, so one task is never judged by the last one's action.
function TaskCleanupParams(Param: String): String;
var
  App: String;
begin
  App := ExpandConstant('{app}\RamOptimiser.exe');
  StringChangeEx(App, '''', '''''', True);
  Result := '-NoProfile -NonInteractive -Command "$app=''' + App + '''; ' +
    '$s=New-Object -ComObject Schedule.Service; $s.Connect(); $f=$s.GetFolder(''\''); ' +
    'foreach ($t in @($f.GetTasks(1))) { ' +
    'if ($t.Name -ne ''RamOptimiser'' -and $t.Name -notmatch ''^RamOptimiser-S-1-[0-9-]+$'') { continue }; ' +
    '$a=$null; try { $a=@($t.Definition.Actions) } catch { continue }; ' +
    'if ($a.Count -eq 1 -and $a[0].Type -eq 0 -and $a[0].Path.Trim([char]34) -eq $app) { try { $f.DeleteTask($t.Name, 0) } catch { } } }"';
end;

// The app's single-instance mutex is Local\RamOptimiser-<user SID>, and Inno Setup has no constant for a SID, so
// this lists one name per account with a profile on this PC. Restart Manager closes the app during setup, but not
// during uninstall, which is why InitializeUninstall checks these names. Only key names made of S, digits and
// dashes are used (ProfileList also holds .bak keys).
function AppMutexes: TArrayOfString;
var
  Sids: TArrayOfString;
  I, J, N: Integer;
  Ok: Boolean;
begin
  SetArrayLength(Result, 0);
  if not RegGetSubkeyNames(HKLM64, 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList', Sids) then
    Exit;
  N := 0;
  for I := 0 to GetArrayLength(Sids) - 1 do
  begin
    Ok := (Length(Sids[I]) > 4) and (Copy(Sids[I], 1, 4) = 'S-1-');
    for J := 1 to Length(Sids[I]) do
      if not (((Sids[I][J] >= '0') and (Sids[I][J] <= '9')) or (Sids[I][J] = '-') or (Sids[I][J] = 'S')) then
        Ok := False;
    if Ok then
    begin
      SetArrayLength(Result, N + 1);
      Result[N] := 'Local\RamOptimiser-' + Sids[I];
      N := N + 1;
    end;
  end;
end;

const
  SYNCHRONIZE = $00100000;
  ERROR_ACCESS_DENIED = 5;

function OpenMutex(dwDesiredAccess: Cardinal; bInheritHandle: Integer; lpName: String): THandle;
  external 'OpenMutexW@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): Integer;
  external 'CloseHandle@kernel32.dll stdcall';

// Inno's CheckForMutexes reads every OpenMutex failure as "not running". The app's mutex lets in only its own
// user and SYSTEM, so an administrator who approved this uninstall for a standard user would not see that user's
// copy. Access denied therefore counts as running. Local\ names are per session: a copy in another signed-in
// session is not seen either way.
function AppRunning: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  H: THandle;
begin
  Result := False;
  Names := AppMutexes;
  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    H := OpenMutex(SYNCHRONIZE, 0, Names[I]);
    if H <> 0 then
    begin
      CloseHandle(H);
      Result := True;
      Exit;
    end;
    if DLLGetLastError = ERROR_ACCESS_DENIED then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  while AppRunning do
    if SuppressibleMsgBox('RAM Optimiser is running. Close it (right-click its tray icon, then Exit) and click OK, or click Cancel to stop.',
      mbError, MB_OKCANCEL, IDCANCEL) <> IDOK then
    begin
      Result := False;
      Exit;
    end;
end;

// /DIR= on the command line can still name another folder. Refuse it: the protected folder is the point.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if CompareText(ExpandConstant('{app}'), ExpandConstant('{autopf}\RAM Optimiser')) <> 0 then
    Result := 'RAM Optimiser installs only to ' + ExpandConstant('{autopf}\RAM Optimiser') +
      ', a folder only administrators can change, because its start-up task runs the app as administrator.';
end;

// The app's machine-wide records (Power Clean's journal, the Startup tab's service records). Per-user settings in
// %LOCALAPPDATA%\RamOptimiser are kept.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteKeyIncludingSubkeys(HKLM64, 'SOFTWARE\RamOptimiser');
end;
