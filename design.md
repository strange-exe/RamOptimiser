# RAM Optimiser GUI: design record

## Brief
Desktop system utility (a window plus a tray icon) for everyday Windows users who feel their PC is short
on RAM. Its main job is to show memory state at a glance, clean in one click, and set auto-clean. It should feel calm, precise,
trustworthy and honest: not a "PC booster" (no rockets, neon, fake scores or scare red).

## Chosen theme: A, Fluent Quiet (picked by the owner on 2026-11-11)
Chosen from three rendered options (A Fluent Quiet, B Graphite and Brass, C Memory Map). The owner chose A because
an app that looks like part of Windows 11 earns trust fastest, and it has no fonts to bundle.
References (awesome-design-md, fetched 2026-11-11): Raycast for the compact form (a ladder of surface colours instead of shadows,
1 px hairlines, radii 4 to 8), Cal.com for the dense settings list (rows split by hairlines, near-monochrome, one
action colour). Only these style traits were borrowed, not either brand's identity.

### Tokens
| Token | Light | Dark |
|---|---|---|
| surface (window) | #F3F3F3 | #202020 |
| card | #FBFBFB | #2B2B2B |
| border (hairline) | #E3E3E3 | #3B3B3B |
| window border | #CFCFCF | #444444 |
| control border | #868686 | #9A9A9A |
| text | #1A1A1A | #FFFFFF |
| muted text | #5C5C5C | #C8C8C8 |
| accent (primary fill, gauge fill, accent text) | #005FB8 | #60CDFF |
| on accent | #FFFFFF | #000000 |
| gauge track background | #E6E6E6 | #3D3D3D |
| secondary button background | #FBFBFB | #333333 |
| ok | #0F7B0F | #6CCB5F |
| danger | #C42B1C | #FF99A4 |
| focus ring | #1A1A1A | #FFFFFF |

Type: Segoe UI Variable Display for the big number and headings, Segoe UI Variable Text for body. Windows 10 falls back to
Segoe UI. Big number: 48 px, weight 600, tracking -0.5. Headings: weight 600. Body: 14 px. Primary button: 40 px tall,
14 px, weight 600.
Shape: radius 4 for controls, 8 for cards. No shadows: cards separate by surface colour and a 1 px hairline.

### Contrast (computed in-page by script, 2026-11-11; all pass)
Body text 16.82 (light) / 14.16 (dark). Muted text 6.46 / 8.46. Button label 6.31 / 11.67. Button fill against the window 5.68 / 9.06.
Gauge fill against the track 5.05 / 6.04. Track edge 3.52 / 5.03. Ok text 5.26 / 6.98. Danger text 5.47 / 6.97. Focus ring 16.82 / 14.16.

## Reading order
- Main window: 1) load gauge (percent plus "13.7 GB of 15.2 GB in use"), 2) the Clean memory button, 3) the breakdown
  rows (Available, Free, Standby cache, Modified, File cache), 4) the auto-clean row at the bottom.
- Settings: auto-clean group (switch, threshold, interval) first, then the areas list, then the app options (theme, start with
  Windows, close to tray).

## Rules
- Status is never shown by colour alone: an icon plus text ("ok", "failed: ...", "skipped: ...").
- Every control has an accessible name and a visible focus ring, and works by keyboard.
- Follows the system light/dark setting, which the user can override. High-contrast mode uses system colours.
- Motion: only state changes (the gauge value eases out over 200 ms). None at all when Windows animations are off.

## Redesign: B, Memory map (picked by the owner on 2026-11-11: "B. Memory map")
Chosen over A Ring and C Ten minutes (preview: .claude/design/gui-options.html). Supersedes theme A's colours.
References (awesome-design-md, fetched 2026-11-11): ClickHouse (figures as flat text, mono numbers, hairlines, no
shadows) and Vercel (ink primary instead of a colour, 600 display ceiling with tight tracking, inset hairline rings).
Only style traits are borrowed.

### Layout (main window, top to bottom)
1. Headline: load percent (56 px, 600, tracking -2) and "of 15.2 GB in use" (muted) on one baseline.
2. Memory map: one 44 px bar, radius 8, 1 px hairline, segments In use / Modified / Standby / Free sized by bytes.
3. Rows aligned to the map: 12 px swatch, label ("In use", "Modified, waiting to be written", "Standby, given back on
   demand", "Free"), value in Cascadia Mono (fallback Consolas) right-aligned; after a clean a signed change
   ("-2.8 GB", "+2.7 GB") in the key colour next to each value. File cache stays as a quiet fifth row.
4. Clean memory: full-width primary (ink fill), 40 px, radius 8.
5. One admin line when not elevated: "Needs administrator rights to clean." plus a "Restart as administrator" link.
   The old banner card and the second message under the button are gone.
6. Result: "Freed 2.8 GB" (20 px, 600) plus one muted sentence; per-area results stay available but the result
   no longer pushes auto-clean off-screen.
7. Auto-clean row pinned at the bottom: title, muted condition, switch, Settings button.

### Tokens (B)
| Token | Light | Dark |
|---|---|---|
| surface | #FAFAFA | #0F0F0F |
| card | #FFFFFF | #171717 |
| hairline | #EBEBEB | #2A2A2A |
| window border | #D4D4D4 | #3A3A3A |
| control border | #8F8F8F | #8A8A8A |
| text | #171717 | #EDEDED |
| muted | #575757 | #A3A3A3 |
| primary fill / on primary | #171717 / #FFFFFF | #EDEDED / #0F0F0F |
| map: in use / modified / standby / free | #171717 / #6E6E6E / #B8B8B8 / #E3E3E3 | #EDEDED / #9A9A9A / #5C5C5C / #2E2E2E |
| key (change figures, the one colour) | #C24E00 | #FF9A52 |
| ok / danger | keep theme A values | keep theme A values |
Contrast in the preview (light): text 17.18, muted 6.92, button label 17.93. Dark and the key colour must be
re-checked in-app by script.

### Contrast (B, computed by script from Light.xaml / Dark.xaml, 2026-11-11)
| Pair | Light | Dark | Needs |
|---|---|---|---|
| Body text on window | 17.18 | 16.37 | 4.5:1 |
| Body text on card | 17.93 | 15.31 | 4.5:1 |
| Muted text on window | 6.92 | 7.60 | 4.5:1 |
| Muted text on card | 7.23 | 7.11 | 4.5:1 |
| Clean memory label on ink fill | 17.93 | 16.37 | 4.5:1 |
| Ink fill against window (button edge) | 17.18 | 16.37 | 3.0:1 |
| Disabled label on disabled fill | 6.34 | 6.53 | 4.5:1 |
| Key colour (row changes) on window | 4.59 | 9.12 | 4.5:1 |
| Ok text on card (area results) | 5.44 | 8.83 | 4.5:1 |
| Danger text on card | 5.66 | 8.83 | 4.5:1 |
| Danger text on window | 5.42 | 9.44 | 4.5:1 |
| Focus ring against window | 17.18 | 16.37 | 3.0:1 |
| Control border against window | 3.10 | 5.55 | 3.0:1 |
| Control border against card | 3.23 | 5.19 | 3.0:1 |
| Map ring (control border) against window | 3.10 | 5.55 | 3.0:1 |
| Map: in use against window | 17.18 | 16.37 | 3.0:1 |
| Map: modified against window | 4.89 | 6.81 | 3.0:1 |
| Map: standby against window | 1.90 (below) | 2.87 (below) | 3.0:1 |
| Map: free against window | 1.23 (below) | 1.41 (below) | 3.0:1 |
| Map: in use next to modified | 3.52 | 2.40 (below) | 3.0:1 |
| Map: modified next to standby | 2.57 (below) | 2.38 (below) | 3.0:1 |
| Map: standby next to free | 1.55 (below) | 2.03 (below) | 3.0:1 |
| Hairline against window (decorative) | 1.14 | 1.34 | none |

The map's outer edge is a 1 px ring in the control-border colour (3:1 in both themes) instead of the hairline, and parts
are split by 2 px gaps in the window colour. Neighbouring parts and the light parts are below 3:1 against each other: the
map is never the only carrier of its values (each row and the map's accessible name give every part as text, in the same
left-to-right order), so the palette is kept as picked. Raising standby/free to 3:1 is an open option for the owner.

### Motion (owner rule: entrances ease out, exits ease in, interface spring with slight overshoot, nothing linear)
- Map segments and figures: 700 ms, BackEase EaseOut amplitude 0.3 on segment widths; figures tween with QuinticEase EaseOut.
- Result block: enters 320 ms QuinticEase EaseOut (fade + 8 px rise); leaves 200 ms CubicEase EaseIn.
- Button press: scale 0.97, 120 ms BackEase EaseOut.
- All motion off (instant) when SystemParameters.ClientAreaAnimation is false.

## Redesign 2: A, Windows native (picked by the owner on 2026-11-11: "A, Windows native")

Supersedes B's layout and tokens; B's motion rules above still apply. Why: the owner found the single-column window
"not user friendly at all". References, per facet: Windows 11 Settings / Fluent 2 for the shell and colours, Linear's
issue list for the dense Startup list (verdict as glyph plus words, one action per row), Stripe's settings forms
for grouped cards with a title, a one-line description and the control on the right.

### Layout
- Three tabs (Ctrl+1/2/3; Ctrl+Tab also works): Memory, Startup, Settings. Window 460 wide (440 to 720), 720 tall,
  clamped to the work area.
- Memory, in reading order: load % (44 px) and "X of Y in use"; the memory map with a 2x2 legend; committed memory
  (the commit charge, with a 4 px bar and a note that cleaning does not lower it); Clean memory; the Power Clean
  note; the admin line; the last-clean line (check, warning or dash glyph plus "Last clean at 14:32 freed 2.0 GB.
  Load went from 81% to 69%." and failures counted), with per-area results behind a disclosure; the top five apps
  by memory (name, process count, figure, thin bar against the largest); the auto-clean switch.
- Startup: an Apps/Services segment with counts, a search box (Ctrl+F), a verdict filter with counts and a sort
  (verdict or name). Rows: a chevron for details, the name, the verdict as glyph plus words, and on the right a
  switch meaning "starts with Windows" or the reason it can't change (Protected by Windows, Needs admin, Left on,
  Left alone, Not at startup). A failed change shows under its own row (title, reason, "It is still on/off.",
  dismiss) and is announced. Loading (skeletons), empty and no-match states are distinct.
- Settings: cards for Auto-clean (switch, a threshold slider saved 400 ms after it rests, a schedule), What to
  clean (four areas shown, four behind "Show 4 more areas", opened when one of them is chosen), Power Clean (the
  service list behind "Services to stop: N of M chosen", counting installed services only), and App.

### Tokens (A)
| Token | Light | Dark |
|---|---|---|
| Surface / Card | #F3F3F3 / #FBFBFB | #202020 / #2B2B2B |
| Hairline / WindowBorder / ControlBorder | #E5E5E5 / #CFCFCF / #868686 | #3B3B3B / #444444 / #9A9A9A |
| Text / MutedText | #1A1A1A / #5C5C5C | #FFFFFF / #C8C8C8 |
| Accent / OnAccent | #005FB8 / #FFFFFF | #60CDFF / #101010 |
| TrackBackground / TrackEdge | #EDEDED / #EDEDED | #333333 / #333333 |
| Ok / Caution / Danger / DangerBackground | #0F7B0F / #9D5D00 / #C42B1C / #FDE7E9 | #6CCB5F / #FCE100 / #FF99A4 / #442726 |
| MapInUse / MapStandby / MapFree | #005FB8 / #CFE1F3 / #FBFBFB | #60CDFF / #1F4A63 / #2B2B2B |
| MapModified | accent hatch on Card | accent hatch on Card |
TrackEdge is the 1 px edge of the commit bar's rest, the top-app tracks and the loading skeletons. It equals the
track fill in light and dark (no visible change); high contrast maps it to WindowText, because the track fill is
Window there, the same as the card. Hover fills still use TrackBackground (owner's visual check pending).
Type: Segoe UI Variable Text (Segoe UI fallback), 12/13/14/28/44, weights 400 and 600; figures tabular.
Radii: controls 4, cards 8. Icons: one 16-unit stroke set (1.5 px, round caps) in Styles.xaml.

### Contrast (A, computed by `.claude/verify/contrast.ps1` from Light.xaml / Dark.xaml, 2026-11-11)
All 23 pairs pass in both themes (46 checks). Lowest: light Caution on Surface 4.73:1, light ControlBorder on
Surface 3.28:1, light Danger on DangerBackground 4.79:1 (glyph, needs 3:1). Standby and free stay below 3:1 against
each other, as in B: the legend and the map's accessible name carry every value as text.

### Verified (2026-11-11)
In-process RenderTargetBitmap renders of every tab in light and dark, plus the last-clean, error-row, expanded-row
and no-match states (never a screen capture); 12 UI Automation and keyboard checks (tab shortcuts, Ctrl+F, search
empty state, row names, Toggle via TogglePattern, slider value); tab order follows reading order. Not run: an
elevated session of the new UI (Clean enabled, live switches), and reduced motion with Windows animations off.

Update 2026-11-17: startup rows show the verdict and full detail at all times. The chevron disclosure was removed
because the owner wanted the use case and warning in view ("it was fine before when it just warned and clearly
mentioned the usecase not hidden in side a dropdown menu").

### Auto-clean status: pause, back-off, holds (wave 2A, 2026-11-17)
One state, derived each time (`AppText.AutoState`), first match wins: Off, NeedsAdmin, Paused, BackingOff (a futile
streak above 0), Armed. It drives four places:
- **Bar at the top of the Memory tab** (`AutoBar`, a polite live region in the sample-error slot; the error wins the
  slot). Paused: pause glyph (Accent), "Auto-clean is paused until 15:41." / "Manual cleans still work." and Resume.
  BackingOff: IconWarn (Caution), "Auto-clean now waits 20 minutes between cleans for high load, because recent
  cleans did not help." and a dismiss button; a dismissed bar returns when the streak changes. No "Reset wait"
  action (owner, adopted from the preview). Read out once on entering each state, not per streak step. Enters with
  opacity and an 8 px drop (320 ms QuinticEase out), leaves with opacity and 4 px (200 ms CubicEase in), instant
  with Windows animations off. Its fill was Card with a ControlBorder edge until wave 5A added the preview's info
  tokens (see below).
- **Auto-clean card line** under the description: "Paused: needs administrator rights"; "Paused until 15:41." with
  a Resume link; the P5 hold reason (woke up, full screen, locked or on battery with a schedule); or the back-off in
  two lines, "Waiting longer: the last 2 cleans left load at 85% or more." and "Next clean for high load after 15:20.
  Closing an app frees more memory than cleaning." Focus on a Resume that disappears moves to the switch.
- **Settings**: "Pause automatic cleans" row ("Manual cleans still work.") with one button that reads "Pause for 1
  hour" or "Resume", so focus stays on it. Collapsed, never disabled, when auto-clean is off or needs admin. The
  cooldown line reads "At most every 5 minutes; now every 20 minutes because recent cleans did not help." while
  backing off; the minutes come from `AutoCleanService.ThresholdCooldown`, so the words match the policy.
- **Tray**: "Auto-clean (paused until 15:41)", then "Pause auto-clean for 1 hour" or "Resume auto-clean", hidden
  (not greyed) when auto-clean is off or needs admin. The back-off tooltip line is P7's (wave 3A, below).

### What happened to the clean (wave 3A: P7, E9, E22, R4+E21)

- **Tray tooltip**: one line ("RAM Optimiser: 72% (11.0 GB of 16.0 GB)") until there is something to say; then
  "RAM Optimiser: 72%", a line break, and the first that applies (`TrayStatus.TooltipLine`): "Auto-clean paused:
  needs admin" > "Auto-clean paused until 15:41" > the last clean went wrong ("Clean at 14:32: 2 areas failed",
  "... 1 area partly failed", "Auto-clean at 14:32 hit its time limit", "... did not finish") > "Auto-clean slowed:
  cleans are not helping" > the last result ("Auto-clean at 14:32 freed 1.2 GB", "Clean at 14:32 freed nothing",
  "Auto-clean at 14:32 was skipped"). An older clean says "yesterday at" or the date. The whole tip stays within the
  63 characters .NET Framework's NotifyIcon allows: line 2 is cut at the last space that fits, never mid-word, and
  left out if no space fits; line 1 is never cut.
- **Tray status item** after Clean memory: the window's own result wording ("Last auto-clean at 14:32: freed 1.2 GB",
  "Last clean at 14:32 freed 2.8 GB. Load went from 81% to 74%.", or the stopped and skipped lines). Hidden before the
  first clean, enabled (a disabled item reads as unavailable), and it opens the window on the Memory tab.
- **Result line** for the two new endings: "Auto-clean at 14:32 hit its 2-minute limit after 3 of 6 areas. Load went
  from 81% to 79%." (warning glyph) and "Last clean at 14:32 was skipped: another RamOptimiser clean was running."
  (dash glyph: nothing ran, nothing failed). No Stop or Cancel button anywhere (owner, 2026-11-17): only automatic
  cleans have a limit, 120 s, checked between steps.
- **Settings > Activity**: two disclosures, "Recent cleans" (this session's last 20 cleans, newest first: "14:32
  Auto-clean (high load): freed 1.2 GB; load 91% to 78%, 80% five minutes later; 4 of 4 areas ok") and "Recent
  activity" (pauses, the back-off starting and ending, memory status failing and recovering; 20 rows). Rows are
  13 px Text with the time in MutedText; empty states read "No cleans yet this session." and "Nothing yet this
  session.". Under them, the cleans.log path as a read-only, selectable TextBox (no Open button: the app may run as
  administrator, and opening a user-writable file from there could start a program another process chose).
- Both lists come from memory; the app never reads cleans.log back. The "five minutes later" figure is the first
  sample 5 to 7 minutes after the clean; a later clean, sleep or a clock change drops it rather than mislabel it.
- No new motion: rows appear in place. No new tokens: Text and MutedText on Card (16.82 and 6.46 light, 14.16 and 8.46
  dark) and the existing TextBox focus line.

Decisions: the pause is 1 hour only and is saved (`AutoCleanPausedUntil=` in settings.ini, UTC, round-trip format),
so it outlasts a restart; on start it is cut to at most an hour from then, because the file is user-writable. A
pause can only stop automatic cleans, never cause one. Turning auto-clean off ends it. When a pause ends or is
resumed, the schedule restarts from then. A manual clean resets the back-off when it brings load under the
threshold and never lengthens it (owner, 2026-11-17).

Contrast of the new pairs (from the tokens above): Caution glyph on Card 5.07 light / 10.73 dark; Accent glyph on
Card above 6 in both; muted text on Card 6.46 / 8.46; ControlBorder edge on Surface 3.28 light. All pass.
Verified: offscreen renders of the paused, backing-off and held states in light and dark (XAML only, no app run).
Not verified: high contrast, Narrator on the tray menu, a real sleep and wake.

### Memory tab additions (wave 5A: R5, U15, U16, U17, U21, R1 and E8 wiring)

- **Top apps by private memory (R5).** The list ranks by private bytes (`Process.PrivateMemorySize64`, summed per
  name), not the working set: the default `workingsets` area trims exactly that, so right after a clean every app
  looked small and the order shuffled while pages faulted back in. Private bytes include paged-out and compressed
  pages (unlike Task Manager's default "active private working set" column), and a trim does not change them.
  Owner's wording (2026-11-17): columns **Private** (Text, the ranking figure and the bar) and **In RAM** (MutedText,
  the working set now), under the heading line "Ranked by private memory. Cleaning does not shrink it; closing the
  app does." Row name: "msedge: 2.9 GB private, 1.6 GB in RAM, 24 processes". Process names go through
  `AppText.DisplayName` everywhere they show: control, format (bidi, zero-width) and separator characters dropped,
  cut to 64 characters with "...", "(no name)" when empty. Plain text only, never markup.
- **Row tooltips (U17).** Hovering anywhere on a row (transparent fill) shows the full name, then "Private: 2.9 GB.
  Memory only this app can use. A clean does not change it." and "In RAM: 1.6 GB. The part held in physical memory
  now. A clean lowers it for a while, then it grows back." Rows stay out of the Tab order (read-only data); their
  accessible name is the keyboard and screen-reader path. One ToolTip style for the whole app: Text on Card
  (16.82 light, 14.16 dark), ControlBorder edge (3.52 / 5.03 against Card), radius 4, 12 px, max 320 px wide.
- **Shortcuts (U16).** Tabs have tooltips ("Memory (Ctrl+1)") and `AutomationProperties.AcceleratorKey`; the search
  placeholder reads "Search (Ctrl+F)". A 28 px clear button (IconClose, "Clear search (Esc)") shows inside the box
  while it has text; the box's right padding is 34 px so text never runs under it. Esc clears a search with text
  and keeps focus there; on an empty box the key is left alone. Row-error dismiss buttons have a "Dismiss" tooltip.
- **Commit warning (U15).** At 90% of the commit limit the bar's fill swaps Accent for Caution (at once, never a fade
  between the two) and the note under it is replaced by IconWarn (Caution) plus "Close to the limit: apps may fail to
  start or to save work. Cleaning does not lower this; closing apps does." in Text. Off below 87% (hysteresis, so 2 s
  samples do not flicker it), never with an unknown limit or before any figure was read. The line fades in (320 ms
  QuinticEase out) and out (200 ms CubicEase in), instant with Windows animations off; it is a polite live region,
  read out only on Normal to Warning while the window shows. The commit row's name ends ". Close to the limit".
  No red. Contrast: Caution bar on its track 4.49 light / 9.57 dark; Caution glyph on Card 5.07 / 10.73.
- **Will a clean help (R1 wiring).** One line under Clean memory (after the admin line), glyph plus words from
  `AppText.CleanAdvice`: IconWarn (Caution) with Text for low memory, IconInfo (MutedText) with
  MutedText for "not short" and "plenty". Display only: it never gates auto-clean (owner, 2026-11-17). A different
  advice shows only after two equal samples in a row (`AdviceSmoother`); the first reading shows at once. Hidden
  unless the figures are live (a failing read hides it, and the next good read shows its own at once). The line
  never repeats the commit warning: `AppText.AdviceFor` swaps the engine's commit-bound advice for Windows' own
  low or plenty signal, as the preview's under-pressure state does, since the warning is always on at 90% or more.
- **Process names.** `SafeText.Name` (src/Core.cs) is the one cleaner for the window, `ramopt top` and the
  per-process trim summary: it walks code points, so supplementary format characters (tag characters) and
  unpaired surrogates go too, and terminal escape sequences lose their control character. `top --json` keeps the
  raw name (the JSON writer escapes every control, format and separator character); it is data, not display.
  Startup app rows use the same cleaner for their own names (`AppText.StartupLabel`). The 'Runs <path>' line is
  the row's identity check, so it shows each hidden character as '?' (`SafeText.Marked`) instead of dropping it.
- **Locked controls (U21).** The last selected area stays enabled and in the Tab order; unticking it is refused (the
  box stays ticked, nothing is saved) and "At least one area must stay selected, so Process working sets stays on."
  shows under the list as a polite live region, read out on every attempt. While locked the box has ItemStatus
  "Must stay selected" and the sentence in its help text. Clean memory and Start with Windows stay disabled (the
  standard pattern), with `ToolTipService.ShowOnDisabled` and their reason as a tooltip.
- **Timing (E8 wiring).** Each per-area row ends with its time in MutedText ("1.8 s", "<0.1 s"); a skipped area shows
  none; the row's name ends ", 1.8 s". Under the rows: "Took 3.4 s.".
- **Info-tinted bar (2A carry-over).** New tokens InfoBackground (#EAF2FB light, #1F2D3A dark) and InfoBorder
  (#B9D2EC, #34506A; Window and WindowText in high contrast) and a `NoticeBar` style; the auto-clean bar uses it, so
  it reads apart from the danger-tinted error bar. Its edge is decorative (glyph and words carry the state). A pause's
  second line says where it came from: "Paused from the tray for 1 hour.", "Paused from Settings for 1 hour." or
  "Paused before the app restarted.", then "Manual cleans still work."; the origin is forgotten when the pause ends.

Contrast of the new pairs (WCAG formula, from Light.xaml / Dark.xaml; also asserted in tests/MemoryTabTests.cs):

| Pair | Needs | Light | Dark |
|---|---|---|---|
| Text on InfoBackground | 4.5 | 15.41 | 14.05 |
| MutedText on InfoBackground (bar detail) | 4.5 | 5.92 | 8.40 |
| Accent glyph on InfoBackground (pause) | 3 | 5.58 | 7.81 |
| Caution glyph on InfoBackground (back-off) | 3 | 4.65 | 10.64 |
| ControlBorder on InfoBackground (Resume edge) | 3 | 3.22 | 4.99 |
| InfoBorder on Surface (decorative) | none | 1.40 | 1.94 |
| Caution bar on TrackBackground | 3 | 4.49 | 9.57 |
| Text on Card (tooltip) | 4.5 | 16.82 | 14.16 |

Verified: offscreen renders (RenderTargetBitmap from XAML loaded as the app loads it, AppShell's own render methods
called directly, without starting the app) of the calm and under-pressure Memory tab, the locked area, the search
clear button and the top-app tooltip, light and dark. Not verified: high contrast, a live hover, Narrator, an
elevated session, and that real samples keep the top-app order stable across a clean.

### Notices, shortcut, reset and read-out status lines (wave 4A: R6, R7, P16 reset, U9)

- **Notifications (R6).** Owner decision, 2026-11-17: "Opt-in, off by default (Recommended)". A SwitchStyle row in
  the App card, "Notify me when auto-clean has a problem", with the Small note "A Windows notification when an
  automatic clean fails or stops helping. Never for routine cleans." While it is off the old rule holds: no balloon
  ever. On, `Notices` (pure) decides: an automatic clean that failed, partly failed or did not finish ("At 14:32, 2
  areas failed. Open RAM Optimiser to see which."), or the step into the longest back-off ("The last 4 cleans left
  load at 85% or more. Closing an app will free more memory than cleaning."). Never for a manual clean, a good clean,
  a busy skip, or while the window shows (it says the same already). `NoticeGate`: one of each kind an hour. Shown
  through `NotifyIcon.ShowBalloonTip` with the warning icon (Windows turns it into a toast; display time follows the
  accessibility setting); a click opens the Memory tab. Each one shown is logged as `notice-shown`.
- **Shortcut (R7).** No default (owner). Row "Shortcut for Clean memory": a read-only recorder box (150 px, Tag
  "None" placeholder; help text "Press a key combination. Press Delete to remove the shortcut."), a "Remove" link
  while one is set, and a polite live line for refusals. `HotkeyRecorder.Decide` (pure): a modifier alone is
  ignored, Tab and Shift+Tab move on, Alt+F4 and Alt+Space reach Windows, Delete or Backspace removes, Esc cancels;
  anything else must be two of Ctrl, Alt, Shift with A-Z, 0-9, F1-F11 or F13-F24 (no Windows key: reserved; no F12:
  reserved for debuggers). Ctrl+Alt combinations get the AltGr warning. `HotkeyBinding` registers through
  `IHotkeyRegistrar` (only `HotkeyListener` calls RegisterHotKey, on a message-only window, elevated only, with
  MOD_NOREPEAT); a refusal (1409: taken) keeps the previous shortcut and saves nothing. Owner floor: a press within
  60 s of the last clean (the later of the service's last clean and the last shortcut clean) is skipped, logged as
  `hotkey-skipped`, and the tray status item says "Shortcut ignored: the last clean was less than a minute ago." for
  a minute; a clean it starts is logged as `hotkey-clean`. The Clean memory tooltip reads "Clean memory
  (Ctrl+Shift+M)" (5A carry-over, `AppText.CleanButtonTip`), the button's AcceleratorKey is the same keys, and the
  tray item shows them at its right. The window's own Ctrl+1/2/3 and Ctrl+F are not taken while the box has focus.
  While the box has focus the registration is suspended (`HotkeyBinding.Suspend`/`Resume`, the spec's
  Recording(previous) state): a new combination is registered only to check Windows accepts it, then let go until
  focus leaves. Skipped presses write one `hotkey-skipped` record a minute at most (`HotkeyGate.ShouldLogSkip`), so
  a program sending the keys in a loop cannot push real cleans out of cleans.log.
- **Reset (P16, reset only).** An OutlineButton "Reset" (name "Reset settings") opens an inline GroupPanel, not a
  dialog: "Reset all settings? This cannot be undone." with "Reset settings" and "Cancel"; focus goes to Cancel, Esc
  or focus leaving the panel cancels. A reset keeps Start with Windows (the logon task is its record), ends any pause
  (auto-clean goes off), clears the shortcut and notifications, saves, re-applies the theme and the Power Clean
  service rows (ApplySettings diffs against the settings the reset replaced), and says "Settings were reset." in a
  polite live line, adding that services Power Clean stopped stay stopped when there are any. Logged as
  `settings-reset`. When the save fails, the SaveError line is the only message and nothing is logged.
- **Read-out status lines (U9).** Each status line sits in an `app:LiveRegion`: the sample error bar, the startup
  summary, error, empty state and search count, Power Clean's status, the Memory tab's auto-clean card line (read when
  its reason changes, and only when the auto bar above it has no notice, since the bar reads its own) and the new shortcut and reset lines are Polite;
  the restart failure and the settings save failure are Assertive. `AnnounceGate` reads a line only when its text is
  new since it was last hidden, so a sample error repeated every 2 s is read once. The search count ("12 shown",
  "Nothing matches") shows only while the list is narrowed and is read 300 ms after typing stops.

Verified: unit and static tests only (tests/HotkeyResetTests.cs: parsing, recorder decisions, the 60 s floor, the
binding state machine with a fake registrar, notice rules and gate, settings round trip and reset, announce gate and
the XAML live regions and names). Mutation checks: a one-modifier rule and a zero floor each fail the tests. Not
verified: no run of the app, so no real RegisterHotKey, no real notification, no Narrator pass, no render of the new
rows, no high contrast.

### Startup tab: read-only sources, publisher line, typed outcomes (wave 6A: E24, E26, E13, E12 UI)
Owner answers used: the planner's recommended ones (decisions file, 2026-11-17); no Startup-tab preview was drawn.
- **Packaged and RunOnce rows (E24).** Listed with the others (`StartupApps.Query(true)`), read-only. In place of
  the switch: "In Settings" (packaged) or "Runs once", no glyph (like Left alone and Not at startup); the row gets no
  switch action at all. First match wins in `AppText.AppLock`: source lock, Left on, Needs admin. The detail adds
  "Store app. Turn it on or off in Settings, Apps, Startup" or "Your organisation controls whether it starts", and
  "Runs once at the next sign-in, then Windows removes it". A packaged row a policy decides reads "Set by policy"
  instead (Settings can't change it), its verdict line reads "Set by policy: nothing to turn off", and it does not
  on its own bring up the Settings button. A run-once row has no list verdict either: its verdict line reads "Runs
  once: nothing to turn off", its detail drops "If off", and its locked name is "X: Runs once at the next sign-in"
  rather than "starts with Windows". Detail parts join without doubling a full stop ("From Discord Inc. Runs ...",
  `AppText.JoinSentences` and `DetailLine`). The summary counts enabled packaged rows as apps on and
  never counts run-once rows; "safe to turn off" counts only rows with a switch here. An OutlineButton "Open Startup
  settings" under the apps note opens `ms-settings:startupapps` (a constant) while the apps list has a packaged row.
  Row keys include the source (`app|Packaged|Teams`), so a Run value and a packaged task with one name stay apart.
  Two read-only rows may still share a key (two tasks of one package with one name, "!Setup" beside "Setup"); that is
  accepted, since a read-only row has no switch, no error bar and no change flow, the only users of the key.
- **Publisher line and Look closer (E26).** The detail's "From <company>" is now `AppText.PublisherLine`: who
  signed it, or "Not signed; the file says ...", or today's "From ..." when the file was not checked. Rows the list
  knows also end with it, after "Runs <file>". A Tag beside the name, IconWarn in Caution plus the words "Look
  closer", marks an unsigned program (not Windows' own catalog-signed files), a signature that does not check out or
  is blocked, a signer that differs from the named company, and any checked host (rundll32, wscript, powershell)
  that runs another file, Windows' own hosts included, since the check never covers the file it runs. Never colour
  alone; the switch name, or a locked row's name, adds ". Look closer: see who signed it". Display only: it never
  changes Keep or a switch.
- **Error or note under the row (E13).** `RowNote` replaces the title/message pair. Nothing changed (a refusal or a
  failure, including an exception, now `Kind = Failed`): the danger InfoBar with IconError, today's words, "Dismiss
  error". Changed only in part: the NoticeBar (info tint) with IconWarn in Caution, titled with the row's name, the
  StartupChangeNote sentence and "Dismiss note". Both are polite live regions read once (`AnnounceGate`).
- **Journal sentence (E12).** After the start label: "Windows or an updater turned it back on after this app turned
  it off" or "Turned off by someone else after this app changed it, so the app leaves it alone". An elevated load
  whose service read worked queues `StartupServices.TidyJournal()` as its own work item after handing the rows to
  the UI, so the sentence shows on that load, the record then goes, and a Power Clean holding the service lock
  never keeps the list on "Loading".
- **Shims removed.** The temporary flag setters on ServiceAction, StartupService.DisabledByApp and StartupChange,
  and the message-text fallback in `ServiceOutcomes.Of`, are gone: outcomes are typed end to end.

Verified: unit and static tests only (tests/StartupTabTests.cs: detail and publisher lines, the marker, locks,
counts, keys, the settings-link rule, every change kind's note, journal sentences, read-only legacy members, and the
AppShell / MainWindow wiring by source). Integration (2026-11-18): the render harness drew a fixed apps list
(signed known row, unsigned row with an error bar, a Windows host running another file locked "Needs admin" with
the marker, packaged, policy-set packaged and run-once rows, the Settings button) and a partial-change note under
a service row, in light and dark (.claude/verify/shots-6a-startup), and printed each switch's and locked row's
UIA name. Not verified: no real packaged rows, no check that an elevated app can open ms-settings, no high
contrast render, no Narrator pass.

### Preview visuals: history, change, progress, flyout, Mica (package 7A, 2026-11-18)
From `.claude/design/enhancements-preview.html`. Decisions live in `src/App/Logic/Visuals.cs` and
`AppText.Visuals.cs` (tested in `tests/VisualsTests.cs`); WPF only paints them.
- **Load history.** `LoadHistory`: a 300-reading ring (10 minutes at 2 s) plus clean times, this session only,
  filled while hidden; a clock that goes back starts it again. `LoadChart` (FrameworkElement, `OnRender`, no
  library) sits in the load card under the legend: 78 px (64 px plot), labels in a 36 px column on the right.
  Hairline at 100% and the floor (and 50% when the floor is 0), ControlBorder dashed (4 3) at the threshold while
  auto-clean is on, Accent 1.5 px line with round joins (broken across gaps over 30 s), Text 1.5 x 7 px tick per
  clean at the foot, MutedText 12 px labels. The floor is 50%, or 0 once a reading dips under 50, so a dip is
  never flattened. Labels: 100, threshold, floor; the threshold wins over a label within 14 px. Under it, the
  left-edge time and "Now". UIA: Image, named by `AppText.ChartName` ("Load over the last 10 minutes: now 71%,
  between 69% and 85%. Cleans at 14:23 and 14:32."). Not animated.
- **Per-part change.** `PartChanges.From(report)`: signed `AppText.Change` per map part, null for no report, a busy
  skip, a missing snapshot or a map whose detail changed; a two-part map has in use and available only. A 12 px
  muted line under each figure plus `AppText.ChangeNote` ("The + and - figures are what the clean at 14:32
  changed."), kept until the next clean starts; the rows' names gain ", changed by -2.1 GB". Enter: opacity and
  Y 4 to 0, 320 ms QuinticEase, 30 ms apart in map order and the note last (`ChangeStagger`; a part with no
  figure takes no slot; `Motion.To` takes a delay); exit: opacity, 200 ms CubicEase in.
- **Clean progress.** `CleanRequest.Progress` (engine, backward compatible: null by default, exceptions ignored)
  reports the services step, then each chosen area once, in clean order; extra trim-and-flush passes are not new
  steps. AppShell posts each step to the UI thread. `CleanProgressView`: shown from 400 ms, so a quick clean never
  flashes it. A 3 px TrackBackground track with an Accent segment (40%) sweeping by TranslateTransform.X, 1600 ms
  a pass, SineEase in-out, forever; reduced motion: the segment stands still over the first 40%. Fade in 150 ms
  ease out, out 200 ms ease in. Step line "Process working sets (2 of 6)", "Stopping services (1 of 9)". Read out
  once as it appears ("Cleaning memory"); the result line reads the end. No Cancel or Stop (owner decision).
- **Tray flyout.** Left click raises `FlyoutRequested`; the right-click menu is unchanged. `Flyout.xaml`: a 360 px
  layered, borderless, topmost window (no taskbar button), Surface fill, WindowBorder 1 px, radius 8. Caption,
  load headline and mini map, the will-a-clean-help line, Clean memory, Auto-clean line with "Pause for 1 hour" or
  "Resume" (name starts with the visible label, WCAG 2.5.3), the last clean with the window's glyph rule, "Open
  window". `FlyoutPlacement` (physical pixels, PerMonitorV2): beside the taskbar edge the work area is cut from,
  12 px in, centred on the pointer, clamped to the work area; with the pointer off that screen's taskbar
  (`FlyoutPlacement.OnTaskbar`) it goes to the tray end of the primary screen, where Windows shows tray icons.
  Placed again after layout, once the DPI of its screen applies. Tab cycles inside; focus starts on Clean memory
  (Open window when Clean is off); if Clean memory turns off while it has focus, `FlyoutFocus` moves focus to Open
  window and back when the clean ends. Keyboard path: the WinForms NotifyIcon never sets NOTIFYICON_VERSION_4, so
  Enter or Space on the tray icon arrives as a right click (Shell_NotifyIcon remarks,
  https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyicona) and opens the tray
  menu, which has the same actions (Clean, Pause, Open); the flyout is a pointer shortcut. Not checked on a real
  taskbar. Esc, focus loss, a second icon click and Open window close it; `FlyoutGate` stops the icon click that caused the focus loss from reopening it within 300 ms. Stays open while
  a clean runs. Enter: opacity and Y 12 to 0, 250 ms QuinticEase; exit: opacity and Y to 8, 150 ms CubicEase in;
  instant with Windows animations off. Alt+F4 hides it rather than closing it.
- **Mica.** `UseMica`, off by default, written to settings.ini only while on (the file keeps its 13 keys
  otherwise). The row shows from build 22000; the switch works from 22621 (DWMWA_SYSTEMBACKDROP_TYPE = 38,
  DWMSBT_MAINWINDOW = 2) and not under high contrast; the note says which. On: window background and HwndSource
  composition colour transparent, frame extended over the client area, caption colour back to the system default.
  Off after being on: backdrop AUTO, frame and colours restored, then `ApplyWindowFrame`. Never applied, nothing
  changes. Caution text on Mica fails in light (4.47:1, preview report), so Caution stays glyph-only on the window
  surface; no new token pairs were added.

Verified: unit and static tests (tests/VisualsTests.cs), with mutation checks (repeat passes counted as steps,
the gate blocking after Esc, Mica under high contrast, the keyboard placement) each failing the suite. A scratch
render (MainWindow.xaml and Flyout.xaml loaded through the app's loader, no AppShell) drew the chart (floor 50 and
floor 0), the load card with change figures and note, the progress line, the Mica row and the flyout, light and
dark. Integration review fixes: the Mica switch now hands SaveAndApply the settings from before the change (it
used to diff the new settings against themselves, so the backdrop only changed after a restart); flyout focus
(`FlyoutFocus`), the primary-screen fallback and the change stagger were added, and the weak static checks
(reduced motion, Esc, focus loss) were tied to the 7A code and proven by mutation. The render harness `Stage7A`
(light and dark) was run on the integrated branch. Not verified: the flyout shown on a real taskbar (placement,
DPI change between monitors), keyboard activation of the tray icon, Mica on screen, high contrast, Narrator, the
progress bar in motion.

## Website (download page), started 2026-11-17

Owner's pick, in their words: "Instrument is good but colour palette not so good, try obsidian alongside
/aceternity-ui /animate the hero and landing page". So: option 1 (Instrument) layout and type, new obsidian palette.

- References (VoltAgent/awesome-design-md, fetched 2026-11-17), chosen for the hero facet (the memory map leads):
  Raycast (the page is the product, near-black canvas, hairlines, 12 px cards) and Warp (quiet 500-weight display,
  mono for figures).
- Stack: Vite + React + TypeScript + Tailwind v4 + motion, static build in `site/`. Every pinned version was published
  at least 7 days before install.
- Aceternity UI (licence allows end products): Spotlight (hero sweep, obsidian tint), Card Spotlight (cursor sheen
  only; its three.js CanvasRevealEffect is dropped for weight), Sticky Scroll Reveal (clean steps; no rainbow
  gradients, inactive steps are not faded because faded text fails contrast).
- Dark only: obsidian is a dark material, and the site keeps one surface mode like its Raycast reference.
- Type: IBM Plex Sans (400, 500, 600) and IBM Plex Mono (400, 500), self-hosted through @fontsource (OFL).
- Not applied from high-end-visual-design: uppercase tracked eyebrow tags (an unslop sign), glass blur on scrolling
  content, and the Inter/Helvetica ban is moot.

### Obsidian tokens

| Token | Value | Role |
|---|---|---|
| canvas | #08080A | page |
| surface | #0E0E12 | cards, panels |
| surface-2 | #15151B | raised rows, keycaps |
| hairline | #25252D | borders |
| hairline-strong | #3A3A45 | control borders |
| text | #EDEDF2 | primary text, primary button fill |
| muted | #A2A3B0 | secondary text |
| ember | #FF7A45 | the one accent: memory freed, modified list, active step |
| sheen | #8EA9CC | standby memory |
| in-use | #D4D5DE | memory in use |
| free | #2C2C36 | free memory (always labelled in text) |
| ok / care | #6FD3A0 / #F0B45A | startup verdicts, always with an icon and words |

Contrast is computed by `site/scripts/contrast.mjs`: all 18 used pairs pass (lowest text pair: muted on surface-2,
7.27:1; ember on surface 7.45:1).

### Verified (2026-11-17)
Production build in the browser pane at 1440 and 390 px: no console errors, no horizontal scroll (after fixing
single-column grids to `minmax(0,1fr)`), axe-core 4.14.0 with WCAG 2.2 AA and best-practice tags: 0 violations,
skip link visible on focus, focus order follows reading order, download zip, favicon and apple-touch-icon served
with the right bytes, build audit (frontend-build-hardening) clean. Not run: reduced-motion emulation (code paths
read: MotionConfig "user", Reveal and Spotlight static, hero demo shows the final state), Firefox and WebKit,
grayscale/blur shots, real-device phone.
