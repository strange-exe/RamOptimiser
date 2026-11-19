# ramopt

A small Windows command-line memory cleaner, modelled on the cleaning areas of
[Mem Reduct](https://github.com/henrypp/memreduct). It is an independent implementation: no Mem Reduct
code is copied (Mem Reduct is GPL-3.0).

## What it actually does (read this first)

Windows does not waste RAM. Most of what looks like "used" memory is either live process memory or the
**standby list**: a cache of recently used files and pages that Windows hands back instantly when a
program needs it. Task Manager already counts standby as *Available*.

So cleaning is a trade-off, not free memory:

| Area | Effect | Cost |
|---|---|---|
| `workingsets` | Trims every process's resident pages to the standby/modified lists. "In use" drops sharply. | Programs page-fault their memory back in as they touch it; brief slowdowns. |
| `filecache` | Trims the system file cache working set. | Files are re-read from disk. |
| `volumes` | Writes each fixed drive's cached file changes to disk (not in the default set). Usually tens of MB. | A burst of disk writes. |
| `modified` | Writes dirty pages to disk so they become standby. | Disk writes. |
| `standby-low` | Drops the lowest-priority cached pages. | Small. |
| `standby` | Drops the **whole** cache (not in the default set). | Apps and files load slower until the cache refills. |
| `combine` | Merges identical pages (Windows 10+). | CPU for a moment. |
| `registry` | Flushes registry changes to disk (Windows 8.1+). | Small. |

It helps when a machine is genuinely short of memory (high load, paging) and one large app needs room
now, or when a buggy driver or app leaves a bloated standby list. On a healthy machine it makes the
numbers look better and the next few minutes slightly slower.

## Use

Build (needs only the C# compiler that ships with Windows, .NET Framework 4.8):

```powershell
.\build.ps1 -Test
```

Run the tests from a normal (non-administrator) terminal. They write and delete files in folders under `%TEMP%`,
which other programs you run can tamper with, so the test runner refuses to run as administrator and exits with
code 3. No test needs administrator rights. To run them elevated anyway, set `RAMOPT_TESTS_ALLOW_ELEVATED=1` first.

### Desktop app: `bin\RamOptimiser.exe`

A window and a tray icon. The tray icon shows the current memory load. Click it for a small panel with the load,
**Clean memory**, auto-clean with **Pause for 1 hour** (or **Resume**), the last clean and **Open window**; Esc or
a click elsewhere closes it. Right-click it for the menu.

Three tabs (Ctrl+1, Ctrl+2, Ctrl+3; hover a tab to see its shortcut):

- **Memory** shows the load, a map of in use, modified, standby and free memory, committed memory against its limit,
  a **Clean memory** button, what the last clean freed, and the five apps using the most memory.
  - Under **Clean memory**, one line says whether a clean would help now, from Windows' own low and high memory
    signal and the commit level. It is advice only: it never decides when auto-clean runs.
  - When committed memory reaches 90% of its limit, its bar changes colour and a warning with an icon says that cleaning does not
    lower it and closing apps does. The warning goes away below 87%.
  - The apps are ranked by private memory (memory only that app uses), which a clean does not change, so the order
    holds after a clean. **In RAM** is the part held in physical memory now. Hover a row for the full name and what
    each figure means.
  - The per-area results of a clean show how long each area took, and the whole clean.
  - **Load, last 10 minutes** draws the load for this session (kept in memory only, never saved), with the
    auto-clean threshold as a dashed line and a tick for each clean. Screen readers hear it as one sentence: the
    range, the load now and when the cleans were.
  - After a clean, each part of the map shows what that clean changed under its figure (for example -2.1 GB
    in use, +400.0 MB free), until the next clean starts.
  - While a clean runs for more than a moment, a bar and a line under **Clean memory** say which step it is on
    ("Process working sets (2 of 6)"). A clean cannot be stopped from here.
- **Startup** lists apps and services that start with Windows, with a verdict for each (safe to turn off, turn off with
  care, keep on), a search box (Ctrl+F; Esc clears it), a filter and a sort. Rows Windows protects, or that need admin
  rights, say so instead of showing a switch. While a service change applies, the other switches wait until it finishes.
  - Store apps that start with Windows (new Teams, Phone Link, WhatsApp and others) are listed too, read-only:
    **Open Startup settings** goes to Settings, Apps, Startup, where Windows turns them on or off. RunOnce entries
    (a program set to run once at the next sign-in) are listed read-only and never removed.
  - Any program can choose its own entry name and path, so names are shown with control, bidi, zero-width and
    line-separator characters removed and look-alike spaces (no-break and the like) shown as plain spaces (long
    names are cut to 64 characters). In the "Runs" path each such character, and each space other than a plain
    one, is shown as `?` instead, so a path padded with hidden characters or a no-break space never reads like a
    real one. Only
    what is shown changes: switches still act on the entry's real name.
  - Each app's details say who signed its program, from its Authenticode signature checked on this PC with no
    network access, and whether that matches the company the file names. An unsigned program, a signature that
    does not check out, a signer that differs from the named company, or a program such as rundll32 or PowerShell
    that runs another file (whose signature is never checked) gets a **Look closer** marker. The marker
    is information only: it never locks or unlocks a switch. Windows' own files, which are signed through a
    catalog, read "signature not checked".
  - When a service change works only in part (for example, it won't start with Windows but other services keep it
    running until a restart), a note under the row says so. When Windows or an updater turns a service back on
    after this app turned it off, its details say so once; with admin rights the app then forgets its record.
- Opened without admin rights, the app shows status only, plus a **Restart as administrator** link. The tray menu
  offers the same restart and says when auto-clean is paused for lack of admin rights.
- Auto-clean can be paused for 1 hour from the tray menu (**Pause auto-clean for 1 hour**, then **Resume
  auto-clean**) or from Settings. Manual cleans still work while it is paused. The pause is saved, so it outlasts a
  restart of the app, but it never lasts more than an hour from the time it is read back.
- When automatic cleans keep leaving load at or above the threshold, auto-clean waits longer between cleans for
  high load (up to 16 times the cooldown). The Memory tab says so, with the time of the next one; Settings shows
  the current wait. A manual clean never adds to this back-off, but like any clean it starts the wait again.
  Automatic cleans also wait in the first 2 minutes
  after the PC wakes and while a full-screen app runs; scheduled ones wait while the PC is locked or on battery.
  The auto-clean card says why.
- **Settings** has these options:
  - auto-clean when load reaches 50 to 99%, and/or on a schedule
  - which areas to clean
  - theme: System, Light or Dark
  - **Use the Windows 11 backdrop** (off by default, shown on Windows 11 only): the Mica backdrop, tinted by your
    wallpaper, behind the window. It needs Windows 11 version 22H2 or later and is not used with high contrast;
    otherwise the window stays solid.
  - start with Windows (a logon task, so it can run as administrator)
  - closing the window hides it to the tray
  - **Notify me when auto-clean has a problem** (off by default): a Windows notification from the tray icon when
    an automatic clean fails, partly fails or does not finish, or when cleans stop helping (auto-clean has reached
    its longest wait). Never for manual cleans, cleans that went well, or while the window is on screen, and at most
    one of each kind an hour. Clicking it opens the Memory tab. While it is off, the app never shows a notification.
  - **Shortcut for Clean memory** (none by default): click the box and press the keys. It needs two of Ctrl, Alt
    and Shift with a letter, a digit or F1 to F24 (not F12, which Windows keeps; the Windows key is not allowed).
    Delete removes it and Esc leaves it as it was. The shortcut works anywhere in Windows, but only while the app
    runs as administrator; without admin rights it is saved and marked not active. If another app already uses the
    keys, Windows refuses them and the row says so; the previous shortcut stays. While the box has focus the current
    shortcut is let go, so pressing it there records it rather than cleaning; a combination another app holds never
    reaches the box, so it cannot be recorded. A press less than 60 seconds after the last clean is ignored, shown in
    the tray menu for a minute and logged (once a minute at most), because any program can send keys.
    The shortcut shows in the Clean memory tooltip and next to **Clean memory** in the tray menu.
  - **Reset settings**: asks first, then puts every setting back to its default (auto-clean off, no shortcut,
    notifications off). Start with Windows is left as it is, and services Power Clean stopped stay stopped until you
    restore them or quit.
- `RamOptimiser.exe --tray` starts hidden in the tray. Launching it again brings the open window to the front.
- Settings are stored in `%LOCALAPPDATA%\RamOptimiser\settings.ini`. Crashes are logged to `error.log` in the same folder.
  The app refuses to use that folder if it is a link (junction or symbolic link), if `settings.ini` is a link
  or has a second hard link, or if the folder is owned by anyone other than you, Administrators or SYSTEM.
  When Windows reports `%LOCALAPPDATA%` as a device path (`\\?\...`, `\\.\...`) or a named pipe
  (`\\server\pipe\...`, `\\server\IPC$\...`), the app treats it like an account with no local app data folder and writes no clean log;
  a normal drive or network share is fine. With no such folder, `settings.ini` and `error.log` go to a
  `RamOptimiser` folder inside the folder the app was started from (for an app started as administrator, often
  `C:\Windows\System32`).
  The minimum wait between automatic cleans is 60 seconds, whatever the file says. `NotifyProblems` (`true` or
  `false`) is the notification switch and `CleanHotkey` the shortcut as text (`Ctrl+Shift+M`; empty for none); a
  shortcut the app would not accept from the keyboard is read as none. `UseMica=true` turns the backdrop on; the
  line is written only while it is on.
  When a clean leaves memory load at or above the threshold (the memory is really in use), the wait doubles
  after each such clean, up to 16 times the setting, and returns to normal once load drops below the threshold.
- Services Power Clean stops are started again when you quit the app. Each one is first recorded in the registry,
  under `HKLM\SOFTWARE\RamOptimiser\PowerClean\<your SID>`, so if the app crashes, is ended, or Windows signs out
  first, your next start as administrator starts them again and says so under **Clean memory**. Each account has
  its own list, so one administrator's start never restarts services another administrator's session stopped.
  Only administrators can change that key, and only names from Power Clean's own list are read from it.
  Older versions kept this list in `stopped-services.txt` in the same folder as the settings. That file is no
  longer read or deleted: any program you run could write it. A leftover one does nothing and can be deleted.
- The app creates `HKLM\SOFTWARE\RamOptimiser` (used by Power Clean and by the Startup tab's service changes) so
  that only Administrators and SYSTEM can change it, with read access for Users. Before it reads or writes there,
  it checks each key it opens on the way to that record (`RamOptimiser`, then `PowerClean\<your SID>` or
  `ServiceStartup`): each must be owned by Administrators or SYSTEM, no one else may change it, and none may be a
  registry link. Other keys under `RamOptimiser` are not looked at. If a check fails, that record is not used: Power
  Clean stops no services and logs why in `error.log` (the first refusal in each run); the Startup tab shows no service as turned off by the app
  (so none can be turned back on there), logs why in `error.log`, and turning a service off fails with the reason
  shown.
- Hidden in the tray, the app updates only the tray icon and hands back the memory its window was using.
- The tray tooltip gets a second line when there is something to know: auto-clean is paused or needs admin, the
  last clean failed or hit its time limit, auto-clean is waiting longer, or what the last clean freed. The tray menu
  shows the last clean's result under **Clean memory**; choosing it opens the window on the Memory tab.
- An automatic clean stops after 2 minutes, at the next step boundary, and says how far it got; a manual clean has no
  limit. If `ramopt` is cleaning at that moment, the app's clean is skipped and the result line says so (the two
  never clean at once).
- Every clean the app runs is added to `cleans.log` in the same folder (see Clean log below).
  **Settings > Activity** lists this session's cleans, with the load five minutes after each, and auto-clean events
  (pauses, the back-off, memory status that could not be read). Those lists come from memory: the app never reads
  the file back. The file's path is shown there as text to copy; the app never opens it.

#### Before you turn on Start with Windows

The logon task starts `RamOptimiser.exe` **as administrator, without a prompt**, from the path it was
registered with. Anyone who can replace that file, or add a DLL next to it, gets their code run as
administrator at your next sign-in. So first copy the two exes to a folder only administrators can change,
such as `C:\Program Files\RamOptimiser\`, and enable the option from that copy. Don't enable it from a
build folder like `bin\` inside your user profile or a downloads folder.

The task is registered through the Task Scheduler API with a fixed definition: it runs only at your sign-in
(it cannot be started on demand), the path must be a local drive path (no network share, no `%` variables),
and the app shows the option as on only when the registered task still runs this exact program with
`--tray`, for your account. Both exes load system DLLs from `System32` only.

Each account gets its own task, named `RamOptimiser-<your SID>` in the Task Scheduler root folder, so one
account turning the option on or off never changes another's. Older versions used one task named
`RamOptimiser`: if that task is yours, the app shows it and replaces it with the per-account task when you
turn the option on (or removes it when you turn it off); another account's `RamOptimiser` task is left alone.
If a task exists but starts another copy (for example after you moved the exe), or runs with other settings,
Settings says so and names the path; turning the option on replaces it. A task the app is not allowed to read
is reported as unreadable, not as off.

On a PC where you are not an administrator, **Restart as administrator** asks for an administrator's password,
and Windows then runs the app as that administrator's account. The app notices (the restart passes your SID with
`--launched-by`), says it is running as that account, with that account's settings and start-up task, and turns
off the Start with Windows switch so it cannot set up a task for the wrong account.

### Command line: `bin\ramopt.exe`

From an **elevated** terminal for anything that cleans:

```powershell
bin\ramopt.exe status                      # no admin needed
bin\ramopt.exe clean                       # default areas (all but full standby)
bin\ramopt.exe clean --areas workingsets,standby-low
bin\ramopt.exe clean --all                 # adds the full standby purge and the drive flush
bin\ramopt.exe watch --threshold 85        # clean when load reaches 85%, at most every 5 minutes
bin\ramopt.exe status --json               # one JSON object on stdout
bin\ramopt.exe clean --no-log              # don't add this clean to cleans.log
bin\ramopt.exe top                         # the 10 apps using the most private memory (no admin needed)
bin\ramopt.exe top 20 --by ws              # the 20 apps holding the most RAM right now
bin\ramopt.exe clean --trim each           # trim app by app, sparing the app in front
bin\ramopt.exe clean --trim each --spare obs64,vlc   # also spare these apps
```

`--trim each` (with `clean` or `watch`) trims app by app instead of all at once and leaves the app in front alone;
`--spare LIST` names more exe names to leave alone, and `--no-spare-foreground` trims the app in front too. It needs
the `workingsets` area; `--trim system`, the default, trims every process at once. The desktop app always trims
all at once.

`top [N]` lists 1 to 100 apps (default 10), each app's processes added together. It ranks by private bytes, the
memory only that app uses, because the working set (RAM it holds now, shared pages included) collapses right after
a clean; `--by ws` ranks by working set instead. It reads only names, pids and memory figures, never paths or
command lines.

`clean` and `watch` add one line per clean to `%LOCALAPPDATA%\RamOptimiser\cleans.log` (see Clean log below)
unless `--no-log`; `clean --dry-run` never does. `--json` works with `status`, `top`, `clean`, `clean --dry-run`
and `watch`, and prints JSON on stdout while messages, notes and errors go to stderr:

- `status --json`: one object, `{"v":1,"time",...}` with `totalPhys`, `availablePhys`, `inUse`, `loadPercent`,
  `systemCache`, `lists` (null when the memory lists can't be read), `commit` (null when unknown), `condition` and
  `advice`.
- `top --json`: one `"type":"top"` object, `{"v":1,"type":"top","time",...}` with `by` (`private` or `ws`) and
  `apps`, largest first: each with `name`, `processes`, `private` and `workingSet`, in bytes.
- `clean --dry-run --json`: one `"type":"estimate"` object: `areas` (each with `name`, `kind` of `bytes`,
  `unknown` or `none`, `bytes` (null unless the kind is `bytes`), `upperBound` (true when the figure is "up to"
  rather than "about") and `note`), `inUseDropAtLeast`, `fileCacheDropUpTo` and `advice`.
- `clean --json`: the clean's record, the same line it adds to the log, also when it is skipped because another
  clean is running. `watch --json`: one such line per clean, skipped turns included.

`help` (or `-h`, `--help`, `/?`) ignores anything after it except `--json` and `--no-log`, which are refused with
exit code 1.

`watch` uses the same backoff as the app: a clean that leaves load at or above the threshold doubles the wait
(up to 16 times `--cooldown`, or `--interval` when the cooldown is shorter, so `--cooldown 0` still backs off) until
load drops below it. The wait follows a monotonic clock, so DST and clock changes don't move it. A failed sample or
clean is logged with a timestamp and watching goes on; 5 failed checks in a row, or 5 failed cleans in a row (with
working checks between them), stop it.

`--areas volumes` skips drives that are not ready (for example locked or offline) instead of failing on them.

Exit codes: 0 ok, 1 bad arguments, 2 some area failed or only partly worked (for example one drive refused the
flush), 3 not elevated, 4 `watch` stopped after 5 errors in a row, 5 another clean is running (`ramopt` or the app;
`clean` waits up to 2 s for it first, `watch` skips that turn).

### Clean log

`%LOCALAPPDATA%\RamOptimiser\cleans.log` gets one JSON object per line (UTF-8, `\n`), format version 1:

- `"type":"clean"`: `id`, `time` (UTC), `source` (`gui` for the app, `cli` for `ramopt clean`, `watch` for
  `ramopt watch`), `reason` (`manual`, `threshold`, `interval` or, from `watch`, `low-memory`), `stopped` (`none`,
  `cancelled`, `timed-out` or `busy`), `ms`, `passes`, `before` and `after` (load, in use, commit percent,
  condition), `freed`, `combinedBytes`, `areas` (name, outcome, ms, message) and `services`. An app clean that
  failed before it could report has null `before`, `after` and `freed` and an `error` with the reason.
- `"type":"rebound"`: `ref` (the clean's `id`), `time`, `load`, `inUse`: the load about five minutes after an app
  clean.
- `"type":"event"` (the app): `time`, `source`, `event` (`paused`, `resumed`, `pause-ended`, `backoff-started`,
  `backoff-ended`, `read-failed`, `read-recovered`, `hotkey-clean` (the shortcut started a clean; its own `clean`
  line follows with reason `manual`), `hotkey-skipped` (a press within 60 s of the last clean; at most one a minute), `notice-shown` (a
  notification was shown) and `settings-reset`) and `text`, the line shown under Recent activity.

Fields are only ever added; a breaking change bumps `v`. Area messages and error text are made one line first (line
breaks become spaces; control, bidi and zero-width characters are removed). All text is cut to 200 characters, and
any control, bidi, zero-width or line-separator character left in a value is written as a `\u` escape (a tag character,
which lies outside the 16-bit range, as two: one per surrogate), so a value can't start a new line or act on a
terminal. The same escaping applies to `--json` output. The file is kept under
256 KB, with one older copy in `cleans.log.old`, and is written only after the same folder checks as `settings.ini`.

## How it works

- Measures with documented APIs: `GlobalMemoryStatusEx`, `GetPerformanceInfo`.
- File cache: the documented `SetSystemFileCacheSize((SIZE_T)-1, (SIZE_T)-1, 0)`
  ([docs](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-setsystemfilecachesize)).
- Drive flush: the documented `FlushFileBuffers` on a `\\.\X:` volume handle
  ([docs](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-flushfilebuffers)).
- Memory lists, page combining and registry flush have no documented API. They use
  `NtSetSystemInformation` with information classes 80, 130 and 155 and the structs from the public
  [phnt headers](https://github.com/winsiderss/phnt). These are private Windows interfaces and could
  change in a future release; each failure is reported per area and never stops the others.
- Enables `SeProfileSingleProcessPrivilege` and `SeIncreaseQuotaPrivilege`, which administrators hold.

## Website

`site/` is the download page (Vite, React, Tailwind, motion), a static build with the zip served from the same host.

```powershell
.\build.ps1 -Test                                  # build the app into bin\
powershell -File site\scripts\package.ps1          # zip bin\ into site\public\download, write the SHA-256
cd site; npm ci --ignore-scripts; npm run build    # static site in site\dist
```

`npm run contrast` checks the colour pairs the site uses. Host `site\dist` anywhere static; the page's CSP is in
`index.html` (add `frame-ancestors 'self'` as a response header, since a meta tag can't set it).

## Layout

- `src/Native.cs` P/Invoke declarations
- `src/Core.cs` areas, memory snapshot, privileges, cleaner, auto-clean policy
- `src/Cli.cs` argument parsing and output
- `src/Gui/` non-visual app logic: settings, auto-clean service, elevation, start-up task, single instance, tray icon
- `src/App/` the WPF app (XAML in `src/App/Xaml`, loaded at runtime); `tools/IconGen.cs` makes the app icon at build time
- `tests/` dependency-free test runner (parsing, policy, struct layouts, settings, task XML, app text, live snapshot)
- `design.md` the chosen visual theme, tokens and contrast results

## Licence

MIT. See [LICENSE](LICENSE).
