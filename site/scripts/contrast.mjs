// Computes WCAG contrast for the token pairs the site actually uses, from src/index.css (the single source).
import { readFileSync } from "node:fs";

const css = readFileSync(new URL("../src/index.css", import.meta.url), "utf8");
const tok = Object.fromEntries([...css.matchAll(/--color-([a-z0-9-]+):\s*(#[0-9a-f]{6})/gi)].map((m) => [m[1], m[2]]));

const lum = (hex) =>
  [1, 3, 5]
    .map((i) => parseInt(hex.slice(i, i + 2), 16) / 255)
    .map((v) => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4))
    .reduce((a, v, i) => a + v * [0.2126, 0.7152, 0.0722][i], 0);
const ratio = (a, b) => {
  const [x, y] = [lum(tok[a]), lum(tok[b])];
  return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
};

// [foreground, background, needed, what]
const pairs = [
  ["text", "canvas", 4.5, "body text"],
  ["text", "surface", 4.5, "card text"],
  ["text", "surface-2", 4.5, "raised rows"],
  ["muted", "canvas", 4.5, "secondary text"],
  ["muted", "surface", 4.5, "secondary text in cards"],
  ["muted", "surface-2", 4.5, "secondary text on raised"],
  ["canvas", "text", 4.5, "primary button label"],
  ["ember", "surface", 4.5, "freed figure"],
  ["ember", "canvas", 3, "active step rule"],
  ["ok", "canvas", 4.5, "verdict text"],
  ["care", "canvas", 4.5, "verdict text"],
  ["ok", "surface", 4.5, "verdict text in card"],
  ["care", "surface", 4.5, "verdict text in card"],
  ["inuse", "surface-2", 3, "map segment"],
  ["sheen", "surface-2", 3, "map segment"],
  ["ember", "surface-2", 3, "map segment"],
  ["hairline-strong", "canvas", 1.5, "control border (with text label, informative only)"],
  ["text", "canvas", 3, "focus ring"],
];

let failed = 0;
for (const [fg, bg, need, what] of pairs) {
  const r = ratio(fg, bg);
  const ok = r >= need;
  if (!ok) failed++;
  console.log(`${ok ? "pass" : "FAIL"}  ${r.toFixed(2).padStart(5)}:1  ${fg} on ${bg} (needs ${need}, ${what})`);
}
process.exit(failed ? 1 : 0);
