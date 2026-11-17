// Adapted from Aceternity UI "Spotlight" (spotlight-new), used under the Aceternity licence.
// Changes: obsidian-tinted (low-saturation cool) beams, one sweep that settles instead of an infinite loop
// (WCAG 2.2.2), custom easing, a static frame under prefers-reduced-motion, no z-index above content.
import { motion, useReducedMotion } from "motion/react";

const first =
  "radial-gradient(68.54% 68.72% at 55.02% 31.46%, hsla(225, 30%, 88%, .085) 0, hsla(225, 25%, 60%, .025) 50%, hsla(225, 20%, 45%, 0) 80%)";
const second =
  "radial-gradient(50% 50% at 50% 50%, hsla(225, 30%, 88%, .06) 0, hsla(225, 25%, 60%, .02) 80%, transparent 100%)";
const third =
  "radial-gradient(50% 50% at 50% 50%, hsla(18, 100%, 70%, .045) 0, hsla(18, 100%, 55%, .015) 80%, transparent 100%)";

function Beams({ side }: { side: "left" | "right" }) {
  const sign = side === "left" ? -1 : 1;
  const edge = side === "left" ? "left-0 origin-top-left" : "right-0 origin-top-right";
  return (
    <>
      <div
        className={`absolute top-0 ${side === "left" ? "left-0" : "right-0"}`}
        style={{ transform: `translateY(-350px) rotate(${sign * 45}deg)`, background: first, width: 560, height: 1380 }}
      />
      <div
        className={`absolute top-0 ${edge}`}
        style={{ transform: `rotate(${sign * 45}deg) translate(${-sign * 5}%, -50%)`, background: second, width: 240, height: 1380 }}
      />
      <div
        className={`absolute top-0 ${edge}`}
        style={{ transform: `rotate(${sign * 45}deg) translate(${-sign * 180}%, -70%)`, background: third, width: 240, height: 1380 }}
      />
    </>
  );
}

export function Spotlight({ sweep = 120 }: { sweep?: number }) {
  const reduce = useReducedMotion();
  const ease = [0.22, 1, 0.36, 1] as const;
  return (
    <motion.div
      aria-hidden="true"
      className="pointer-events-none absolute inset-0 overflow-hidden"
      initial={{ opacity: reduce ? 1 : 0 }}
      animate={{ opacity: 1 }}
      transition={{ duration: 1.6, ease }}
    >
      <motion.div
        className="absolute inset-y-0 left-0 w-screen"
        initial={{ x: reduce ? 0 : -sweep }}
        animate={{ x: 0 }}
        transition={{ duration: 3.2, ease }}
      >
        <Beams side="left" />
      </motion.div>
      <motion.div
        className="absolute inset-y-0 right-0 w-screen"
        initial={{ x: reduce ? 0 : sweep }}
        animate={{ x: 0 }}
        transition={{ duration: 3.2, ease }}
      >
        <Beams side="right" />
      </motion.div>
    </motion.div>
  );
}
