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

### Desktop app: `bin\RamOptimiser.exe`

A window and a tray icon. The tray icon shows the current memory load.

Three tabs (Ctrl+1, Ctrl+2, Ctrl+3):

- **Memory** shows the load, a map of in use, modified, standby and free memory, committed memory against its limit,
  a **Clean memory** button, what the last clean freed, and the five apps using the most memory.
- **Startup** lists apps and services that start with Windows, with a verdict for each (safe to turn off, turn off with
  care, keep on), a search box (Ctrl+F), a filter and a sort. Rows Windows protects, or that need admin rights, say so
  instead of showing a switch. While a service change applies, the other switches wait until it finishes.
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
  - start with Windows (a logon task, so it can run as administrator)
  - closing the window hides it to the tray
- `RamOptimiser.exe --tray` starts hidden in the tray. Launching it again brings the open window to the front.
- Settings are stored in `%LOCALAPPDATA%\RamOptimiser\settings.ini`. Crashes are logged to `error.log` in the same folder.
  The app refuses to use that folder if it is a link (junction or symbolic link), if `settings.ini` is a link
  or has a second hard link, or if the folder is owned by anyone other than you, Administrators or SYSTEM.
  The minimum wait between automatic cleans is 60 seconds, whatever the file says.
  When a clean leaves memory load at or above the threshold (the memory is really in use), the wait doubles
  after each such clean, up to 16 times the setting, and returns to normal once load drops below the threshold.
- Services Power Clean stops are started again when you quit the app. Each one is first recorded in
  `stopped-services.txt` in the same folder, so if the app crashes, is ended, or Windows signs out first, the next
  start as administrator starts them again and says so under **Clean memory**. Only names from Power Clean's own
  list are read from that file; anything else in it is ignored.
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
```

`clean` and `watch` add one line per clean to `%LOCALAPPDATA%\RamOptimiser\cleans.log` (see Clean log below)
unless `--no-log`; `clean --dry-run` never does. `--json` works with `status`, `clean`, `clean --dry-run` and
`watch`, and prints JSON on stdout while messages, notes and errors go to stderr:

- `status --json`: one object, `{"v":1,"time",...}` with `totalPhys`, `availablePhys`, `inUse`, `loadPercent`,
  `systemCache`, `lists` (null when the memory lists can't be read), `commit` (null when unknown), `condition` and
  `advice`.
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
  `backoff-ended`, `read-failed`, `read-recovered`) and `text`, the line shown under Recent activity.

Fields are only ever added; a breaking change bumps `v`. Text is cut to 200 characters and escaped, so a message
can't start a new line. The file is kept under 256 KB, with one older copy in `cleans.log.old`, and is written only
after the same folder checks as `settings.ini`.

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
