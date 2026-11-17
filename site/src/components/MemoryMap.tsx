// The product's own picture: a 16 GB machine split into in use, modified, standby and free, as the app's Memory tab
// shows it. Segments move by transform only (scaleX from the left edge, translateX by the running total).
import { motion, useReducedMotion } from "motion/react";

export type Mem = { inuse: number; modified: number; standby: number; free: number };
export const TOTAL_GB = 16;

const parts = [
  { key: "inuse", label: "In use", color: "bg-inuse" },
  { key: "modified", label: "Modified", color: "bg-ember" },
  { key: "standby", label: "Standby", color: "bg-sheen" },
  { key: "free", label: "Free", color: "bg-free" },
] as const;

export const loadPercent = (m: Mem) => Math.round(((m.inuse + m.modified) / TOTAL_GB) * 100);

export function MemoryBar({ mem, duration = 0.9 }: { mem: Mem; duration?: number }) {
  const reduce = useReducedMotion();
  let start = 0;
  return (
    <div className="relative h-3 overflow-hidden rounded-full bg-surface-2 ring-1 ring-hairline">
      {parts.map((p) => {
        const share = mem[p.key] / TOTAL_GB;
        const x = start * 100;
        start += share;
        return (
          <motion.span
            key={p.key}
            className={`absolute inset-y-0 left-0 w-full origin-left ${p.color}`}
            initial={false}
            animate={{ x: `${x}%`, scaleX: share }}
            transition={{ duration: reduce ? 0 : duration, ease: [0.22, 1, 0.36, 1] }}
          />
        );
      })}
    </div>
  );
}

export function MemoryLegend({ mem }: { mem: Mem }) {
  return (
    <dl className="mt-4 grid grid-cols-2 gap-x-6 gap-y-2 text-sm">
      {parts.map((p) => (
        <div key={p.key} className="flex items-center justify-between gap-3">
          <dt className="flex items-center gap-2 text-muted">
            <span aria-hidden="true" className={`size-2.5 rounded-[3px] ${p.color} ${p.key === "free" ? "ring-1 ring-hairline-strong" : ""}`} />
            {p.label}
          </dt>
          <dd className="font-mono tabular-nums text-text">{mem[p.key].toFixed(1)} GB</dd>
        </div>
      ))}
    </dl>
  );
}
