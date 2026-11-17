// Adapted from Aceternity UI "Card Spotlight", used under the Aceternity licence.
// Changes: keeps only the cursor-following sheen (the three.js CanvasRevealEffect is dropped for page weight),
// double-bezel shell, obsidian tokens, and the sheen is decoration only (content never depends on it).
import { motion, useMotionTemplate, useMotionValue } from "motion/react";
import type { MouseEvent, ReactNode } from "react";
import { cn } from "../../lib/utils";

export function CardSpotlight({ children, className }: { children: ReactNode; className?: string }) {
  const x = useMotionValue(-400);
  const y = useMotionValue(-400);
  const mask = useMotionTemplate`radial-gradient(260px circle at ${x}px ${y}px, black, transparent 75%)`;

  function onMove(e: MouseEvent<HTMLDivElement>) {
    const r = e.currentTarget.getBoundingClientRect();
    x.set(e.clientX - r.left);
    y.set(e.clientY - r.top);
  }

  return (
    <div className={cn("group/spot rounded-[22px] border border-hairline bg-surface/60 p-1.5", className)}>
      <div
        onMouseMove={onMove}
        className="relative h-full overflow-hidden rounded-[16px] border border-hairline/70 bg-surface shadow-[inset_0_1px_0_rgba(255,255,255,0.04)]"
      >
        <motion.div
          aria-hidden="true"
          className="pointer-events-none absolute -inset-px opacity-0 transition-opacity duration-500 ease-[cubic-bezier(0.22,1,0.36,1)] group-hover/spot:opacity-100"
          style={{
            background: "linear-gradient(135deg, rgba(237,237,242,0.07), rgba(255,122,69,0.05))",
            WebkitMaskImage: mask,
            maskImage: mask,
          }}
        />
        <div className="relative h-full">{children}</div>
      </div>
    </div>
  );
}
