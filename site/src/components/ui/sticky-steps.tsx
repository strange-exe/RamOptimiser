// Adapted from Aceternity UI "Sticky Scroll Reveal", used under the Aceternity licence.
// Changes: tracks page scroll instead of an inner scroll box (no scroll trap), no rainbow gradients, inactive steps
// stay at full contrast (the active one gets an ember rule and marker instead of the others fading), the sticky
// panel is a render prop so it can show the memory map for the active step, and below lg it becomes a plain list.
import { useMotionValueEvent, useScroll, motion } from "motion/react";
import { useRef, useState, type ReactNode } from "react";
import { cn } from "../../lib/utils";

export type Step = { title: string; body: string; cost: string };

export function StickySteps({ steps, panel }: { steps: Step[]; panel: (active: number) => ReactNode }) {
  const ref = useRef<HTMLDivElement>(null);
  const [active, setActive] = useState(0);
  const { scrollYProgress } = useScroll({ target: ref, offset: ["start 60%", "end 60%"] });

  useMotionValueEvent(scrollYProgress, "change", (p) => {
    const i = Math.min(steps.length - 1, Math.max(0, Math.floor(p * steps.length)));
    setActive(i);
  });

  return (
    <div ref={ref} className="grid grid-cols-[minmax(0,1fr)] gap-12 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.05fr)] lg:gap-20">
      <ol className="space-y-6 lg:space-y-0">
        {steps.map((s, i) => (
          <li key={s.title} className="lg:flex lg:min-h-[52vh] lg:items-center">
            <div className={cn("relative border-l pl-6 transition-colors duration-500", i === active ? "border-ember" : "border-hairline")}>
              <span className="font-mono text-xs text-muted tabular-nums">Step {i + 1}</span>
              <h3 className="mt-2 text-2xl font-medium tracking-tight text-text">{s.title}</h3>
              <p className="mt-3 max-w-[46ch] text-base leading-relaxed text-muted">{s.body}</p>
              <p className="mt-4 text-sm text-text">
                <span className="text-muted">Cost: </span>
                {s.cost}
              </p>
              {i === active && (
                <motion.span
                  layoutId="step-marker"
                  aria-hidden="true"
                  className="absolute top-0 -left-[3px] hidden h-10 w-[5px] rounded-full bg-ember lg:block"
                  transition={{ type: "spring", stiffness: 380, damping: 32 }}
                />
              )}
            </div>
          </li>
        ))}
      </ol>
      <div className="hidden lg:block">
        <div className="sticky top-[22vh]">{panel(active)}</div>
      </div>
    </div>
  );
}
