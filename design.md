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
