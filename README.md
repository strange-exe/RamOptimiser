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
  instead of showing a switch.
- Opened without admin rights, the app shows status only, plus a **Restart as administrator** link.
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
- Hidden in the tray, the app updates only the tray icon and hands back the memory its window was using.

#### Before you turn on Start with Windows

The logon task starts `RamOptimiser.exe` **as administrator, without a prompt**, from the path it was
registered with. Anyone who can replace that file, or add a DLL next to it, gets their code run as
administrator at your next sign-in. So first copy the two exes to a folder only administrators can change,
such as `C:\Program Files\RamOptimiser\`, and enable the option from that copy. Don't enable it from a
build folder like `bin\` inside your user profile or a downloads folder.

The task is registered through the Task Scheduler API with a fixed definition: it runs only at your sign-in
(it cannot be started on demand), the path must be a local drive path (no network share, no `%` variables),
and the app shows the option as on only when the registered task still runs this exact program with
`--tray`. Both exes load system DLLs from `System32` only.

### Command line: `bin\ramopt.exe`

From an **elevated** terminal for anything that cleans:

```powershell
bin\ramopt.exe status                      # no admin needed
bin\ramopt.exe clean                       # default areas (all but full standby)
bin\ramopt.exe clean --areas workingsets,standby-low
bin\ramopt.exe clean --all                 # adds the full standby purge and the drive flush
bin\ramopt.exe watch --threshold 85        # clean when load reaches 85%, at most every 5 minutes
```

Exit codes: 0 ok, 1 bad arguments, 2 some area failed, 3 not elevated.

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
