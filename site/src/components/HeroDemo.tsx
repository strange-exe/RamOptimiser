// Hero focal: a scripted clean on a 16 GB machine. It plays once when it scrolls into view (never loops, so no
// pause control is needed under WCAG 2.2.2) and can be replayed. Reduced motion shows the finished state at once.
// Each pass frees less than the one before, as the app's adaptive passes do; it stops at 3 or when a pass gains little.
import { ArrowCounterClockwise } from "@phosphor-icons/react";
import { animate, motion, useInView, useMotionValue, useReducedMotion, useTransform } from "motion/react";
import { useCallback, useEffect, useRef, useState } from "react";
import { MemoryBar, MemoryLegend, loadPercent, type Mem } from "./MemoryMap";

const before: Mem = { inuse: 12.6, modified: 1.2, standby: 1.4, free: 0.8 };
const passes: { mem: Mem; label: string }[] = [
  { mem: { inuse: 11.4, modified: 0.9, standby: 2.7, free: 1.0 }, label: "Pass 1 of 3: trimming working sets" },
  { mem: { inuse: 11.0, modified: 0.4, standby: 3.3, free: 1.3 }, label: "Pass 2 of 3: writing the modified list" },
  { mem: { inuse: 10.9, modified: 0.2, standby: 3.5, free: 1.4 }, label: "Pass 3 of 3: last gain over 256 MB" },
];
const after = passes[passes.length - 1].mem;
const freedGb = before.inuse + before.modified - (after.inuse + after.modified);

type Phase = "idle" | "running" | "done";

export function HeroDemo() {
  const ref = useRef<HTMLDivElement>(null);
  const inView = useInView(ref, { once: true, amount: 0.5 });
  const reduce = useReducedMotion();
  const [mem, setMem] = useState<Mem>(reduce ? after : before);
  const [step, setStep] = useState(reduce ? "Finished in 3 passes" : "Ready");
  const [phase, setPhase] = useState<Phase>(reduce ? "done" : "idle");
  const load = useMotionValue(loadPercent(reduce ? after : before));
  const freed = useMotionValue(reduce ? freedGb : 0);
  const loadText = useTransform(load, (v) => `${Math.round(v)}%`);
  const freedText = useTransform(freed, (v) => `${v.toFixed(1)} GB`);
  const timers = useRef<number[]>([]);

  const run = useCallback(() => {
    timers.current.forEach(clearTimeout);
    timers.current = [];
    setPhase("running");
    setMem(before);
    load.set(loadPercent(before));
    freed.set(0);
    setStep("Starting clean");
    passes.forEach((p, i) => {
      timers.current.push(
        window.setTimeout(() => {
          setMem(p.mem);
          setStep(p.label);
          const gone = before.inuse + before.modified - (p.mem.inuse + p.mem.modified);
          animate(load, loadPercent(p.mem), { duration: 0.9, ease: [0.22, 1, 0.36, 1] });
          animate(freed, gone, { duration: 0.9, ease: [0.22, 1, 0.36, 1] });
        }, 700 + i * 1300),
      );
    });
    timers.current.push(
      window.setTimeout(() => {
        setStep("Finished in 3 passes");
        setPhase("done");
      }, 700 + passes.length * 1300),
    );
  }, [load, freed]);

  useEffect(() => {
    if (inView && !reduce && phase === "idle") run();
  }, [inView, reduce, phase, run]);
  useEffect(() => () => timers.current.forEach(clearTimeout), []);

  return (
    <div ref={ref} className="rounded-[26px] border border-hairline bg-surface/50 p-1.5 shadow-[0_40px_120px_-40px_rgba(0,0,0,0.9)]">
      <figure
        className="relative overflow-hidden rounded-[20px] border border-hairline/80 bg-surface p-6 shadow-[inset_0_1px_0_rgba(255,255,255,0.05)] sm:p-7"
        aria-label={`Example clean on a 16 GB PC: memory in use drops from ${loadPercent(before)}% to ${loadPercent(after)}%, ${freedGb.toFixed(1)} GB freed in 3 passes.`}
      >
        <div className="flex items-center justify-between text-sm text-muted">
          <span className="flex items-center gap-2">
            <span
              aria-hidden="true"
              className={`size-2 rounded-full ${phase === "running" ? "animate-pulse bg-ember motion-reduce:animate-none" : phase === "done" ? "bg-ok" : "bg-hairline-strong"}`}
            />
            Memory
          </span>
          <span className="font-mono text-xs">16 GB</span>
        </div>

        <div aria-hidden="true" className="mt-6 flex items-end justify-between gap-4">
          <div>
            <div className="text-sm text-muted">In use</div>
            <motion.div className="font-mono text-6xl font-medium tracking-tight tabular-nums text-text sm:text-7xl">{loadText}</motion.div>
          </div>
          <div className="text-right">
            <div className="text-sm text-muted">Freed</div>
            <motion.div className="font-mono text-2xl font-medium tabular-nums text-ember">{freedText}</motion.div>
          </div>
        </div>

        <div aria-hidden="true" className="mt-6">
          <MemoryBar mem={mem} />
          <MemoryLegend mem={mem} />
        </div>

        <div className="mt-6 flex min-h-11 items-center justify-between gap-4 border-t border-hairline pt-4">
          <p aria-hidden="true" className="font-mono text-xs text-muted">{step}</p>
          <button
            type="button"
            onClick={run}
            disabled={phase === "running"}
            className="inline-flex min-h-11 items-center gap-2 rounded-full border border-hairline-strong px-4 text-sm text-text transition-[transform,background-color] duration-300 ease-[cubic-bezier(0.22,1,0.36,1)] hover:bg-surface-2 active:scale-[0.97] disabled:opacity-50"
          >
            <ArrowCounterClockwise aria-hidden="true" weight="light" className="size-4" />
            Replay clean
          </button>
        </div>
      </figure>
      <p className="px-5 pt-3 pb-2 text-xs text-muted">
        An example, not your PC. Programs page some of this back in as they need it.
      </p>
    </div>
  );
}
