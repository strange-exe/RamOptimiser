import { ArrowDown, CheckCircle, Gauge, Power, ShieldCheck, Terminal, Warning } from "@phosphor-icons/react";
import { motion, useReducedMotion } from "motion/react";
import { CardSpotlight } from "./components/ui/card-spotlight";
import { Spotlight } from "./components/ui/spotlight";
import { StickySteps, type Step } from "./components/ui/sticky-steps";
import { CopyHash } from "./components/CopyHash";
import { DownloadButton } from "./components/DownloadButton";
import { HeroDemo } from "./components/HeroDemo";
import { MemoryBar, MemoryLegend, loadPercent, type Mem } from "./components/MemoryMap";
import { Reveal } from "./components/Reveal";
import { formatBytes } from "./lib/utils";
import release from "./release.json";

const ease = [0.22, 1, 0.36, 1] as const;

const steps: (Step & { mem: Mem })[] = [
  {
    title: "Merge identical pages",
    body: "Windows 10 and 11 can keep one copy of pages that hold the same bytes. This runs first so the passes after it start from less.",
    cost: "a moment of CPU",
    mem: { inuse: 12.4, modified: 1.2, standby: 1.4, free: 1.0 },
  },
  {
    title: "Trim what programs hold",
    body: "Every process gives back the pages it isn't touching. In use drops sharply; those pages move to the modified and standby lists.",
    cost: "programs page memory back in as they use it",
    mem: { inuse: 11.2, modified: 1.6, standby: 2.2, free: 1.0 },
  },
  {
    title: "Write the modified list",
    body: "Changed pages are written to disk so they become plain cache. Writing them can refill the compression store, so the trim repeats.",
    cost: "a burst of disk writes",
    mem: { inuse: 11.0, modified: 0.3, standby: 3.6, free: 1.1 },
  },
  {
    title: "Repeat only while it pays",
    body: "Another pass runs only if the last one freed at least 256 MB or 2% of RAM, up to three passes. Then it stops.",
    cost: "about five seconds more than one pass",
    mem: { inuse: 10.9, modified: 0.2, standby: 3.6, free: 1.3 },
  },
  {
    title: "Drop the lowest-priority cache",
    body: "Only the cache Windows values least is released. The full standby purge is off by default, because that cache makes apps open fast.",
    cost: "small",
    mem: { inuse: 10.9, modified: 0.2, standby: 3.5, free: 1.4 },
  },
];

function Nav() {
  return (
    <header className="fixed inset-x-0 top-4 z-40 flex justify-center px-4">
      <nav
        aria-label="Main"
        className="flex w-full max-w-3xl items-center justify-between gap-4 rounded-full border border-hairline bg-canvas/70 py-1.5 pr-1.5 pl-5 backdrop-blur-xl"
      >
        <a href="#top" className="flex min-h-11 items-center gap-2.5 font-medium text-text">
          <img src="/favicon.ico" alt="" width={20} height={20} className="size-5" />
          RamOptimiser
        </a>
        <div className="hidden items-center gap-1 text-sm text-muted md:flex">
          <a href="#how" className="rounded-full px-3 py-2.5 transition-colors hover:text-text">How it works</a>
          <a href="#when" className="rounded-full px-3 py-2.5 transition-colors hover:text-text">When it helps</a>
          <a href="#features" className="rounded-full px-3 py-2.5 transition-colors hover:text-text">Features</a>
        </div>
        <a
          href="#download"
          className="inline-flex min-h-10 items-center rounded-full bg-text px-4 text-sm font-semibold text-canvas transition-transform duration-300 ease-[cubic-bezier(0.22,1,0.36,1)] active:scale-[0.97]"
        >
          Download
        </a>
      </nav>
    </header>
  );
}

function Hero() {
  const reduce = useReducedMotion();
  const enter = (d: number) =>
    reduce ? {} : { initial: { opacity: 0, y: 18 }, animate: { opacity: 1, y: 0 }, transition: { duration: 0.9, delay: d, ease } };
  return (
    <section id="top" className="relative isolate overflow-hidden pt-36 pb-24 md:pt-44 md:pb-32">
      <Spotlight />
      <div className="mx-auto grid max-w-6xl grid-cols-[minmax(0,1fr)] items-center gap-14 px-4 sm:px-6 lg:grid-cols-[minmax(0,1.05fr)_minmax(0,0.95fr)] lg:gap-16">
        <div>
          <h1 className="max-w-[15ch] text-[2.6rem] leading-[1.04] font-medium tracking-[-0.03em] text-text sm:text-6xl lg:text-[4.1rem]">
            Free memory when Windows is short of it
          </h1>
          <motion.p {...enter(0.15)} className="mt-6 max-w-[44ch] text-lg leading-relaxed text-muted">
            A small memory cleaner for Windows. It trims what programs hold, flushes the cache it can, and tells you
            plainly what each clean costs and when it won't help.
          </motion.p>
          <motion.div {...enter(0.3)} className="mt-9 flex flex-wrap items-center gap-x-6 gap-y-4">
            <DownloadButton />
            <a href="#how" className="inline-flex min-h-11 items-center gap-2 text-[15px] text-text underline decoration-hairline-strong underline-offset-4 transition-colors hover:decoration-text">
              How a clean works
              <ArrowDown aria-hidden="true" weight="light" className="size-4" />
            </a>
          </motion.div>
          <motion.p {...enter(0.4)} className="mt-6 font-mono text-[13px] text-muted">
            Windows 10 and 11, 64-bit. {formatBytes(release.size)} zip. Built {release.built}.
          </motion.p>
        </div>
        <motion.div {...(reduce ? {} : { initial: { opacity: 0, y: 40, scale: 0.98 }, animate: { opacity: 1, y: 0, scale: 1 }, transition: { duration: 1.1, delay: 0.2, ease } })}>
          <HeroDemo />
        </motion.div>
      </div>
    </section>
  );
}

function StepPanel({ i }: { i: number }) {
  const s = steps[i];
  return (
    <div className="rounded-[26px] border border-hairline bg-surface/50 p-1.5">
      <div className="rounded-[20px] border border-hairline/80 bg-surface p-7 shadow-[inset_0_1px_0_rgba(255,255,255,0.05)]">
        <div className="flex items-baseline justify-between">
          <span className="text-sm text-muted">After step {i + 1}</span>
          <span className="font-mono text-4xl font-medium tabular-nums text-text">{loadPercent(s.mem)}%</span>
        </div>
        <div className="mt-6">
          <MemoryBar mem={s.mem} duration={0.7} />
          <MemoryLegend mem={s.mem} />
        </div>
      </div>
    </div>
  );
}

function How() {
  return (
    <section id="how" aria-labelledby="how-title" className="scroll-mt-24 border-t border-hairline py-28 md:py-36">
      <div className="mx-auto max-w-6xl px-4 sm:px-6">
        <Reveal>
          <h2 id="how-title" className="max-w-[20ch] text-4xl leading-[1.08] font-medium tracking-[-0.025em] text-text md:text-5xl">
            What a clean does, step by step, and what each step costs
          </h2>
          <p className="mt-5 max-w-[56ch] text-lg leading-relaxed text-muted">
            Windows doesn't waste RAM. Most "used" memory is live data or cache it hands back instantly. Cleaning moves
            memory between lists, so every step has a price.
          </p>
        </Reveal>
        <div className="mt-16 md:mt-20">
          <StickySteps steps={steps} panel={(i) => <StepPanel i={i} />} />
        </div>
      </div>
    </section>
  );
}

function When() {
  const helps = [
    "Memory load stays high and Windows is paging to disk",
    "One big app, a game or a VM, needs room right now",
    "A leaky driver or app has bloated the standby list",
  ];
  const doesnt = [
    "A healthy PC: the numbers look better and the next minutes run slightly slower",
    "Memory that's really in use: it comes back within a minute, and the app tells you so",
    "Speeding up a slow disk or CPU: cleaning can't",
  ];
  return (
    <section id="when" aria-labelledby="when-title" className="scroll-mt-24 border-t border-hairline py-28 md:py-36">
      <div className="mx-auto grid max-w-6xl grid-cols-[minmax(0,1fr)] gap-14 px-4 sm:px-6 lg:grid-cols-[minmax(0,0.8fr)_minmax(0,1.2fr)] lg:gap-20">
        <Reveal>
          <h2 id="when-title" className="max-w-[14ch] text-4xl leading-[1.08] font-medium tracking-[-0.025em] text-text md:text-5xl">
            When it helps, and when it won't
          </h2>
          <p className="mt-5 max-w-[40ch] text-lg leading-relaxed text-muted">
            Most cleaners promise speed. This one tells you when to leave memory alone.
          </p>
        </Reveal>
        <div className="grid grid-cols-[minmax(0,1fr)] gap-6 sm:grid-cols-2">
          <Reveal delay={0.05}>
            <h3 className="flex items-center gap-2 text-lg font-medium text-text">
              <CheckCircle aria-hidden="true" weight="light" className="size-5 text-ok" />
              It helps
            </h3>
            <ul className="mt-5 space-y-4 border-t border-hairline pt-5">
              {helps.map((t) => (
                <li key={t} className="text-base leading-relaxed text-muted">{t}</li>
              ))}
            </ul>
          </Reveal>
          <Reveal delay={0.12}>
            <h3 className="flex items-center gap-2 text-lg font-medium text-text">
              <Warning aria-hidden="true" weight="light" className="size-5 text-care" />
              It won't help
            </h3>
            <ul className="mt-5 space-y-4 border-t border-hairline pt-5">
              {doesnt.map((t) => (
                <li key={t} className="text-base leading-relaxed text-muted">{t}</li>
              ))}
            </ul>
          </Reveal>
        </div>
      </div>
    </section>
  );
}

function StartupRows() {
  const rows = [
    { name: "Microsoft Edge (auto-launch)", verdict: "Safe to turn off", icon: CheckCircle, color: "text-ok", on: false },
    { name: "Xbox Gaming Services", verdict: "Turn off with care", icon: Warning, color: "text-care", on: true },
    { name: "Windows Defender", verdict: "Keep on", icon: ShieldCheck, color: "text-muted", on: true },
  ];
  return (
    <ul className="mt-6 divide-y divide-hairline rounded-xl border border-hairline bg-canvas/60">
      {rows.map((r) => (
        <li key={r.name} className="flex items-center justify-between gap-4 px-4 py-3">
          <div className="min-w-0">
            <div className="truncate text-sm text-text">{r.name}</div>
            <div className={`mt-0.5 flex items-center gap-1.5 text-xs ${r.color}`}>
              <r.icon aria-hidden="true" weight="light" className="size-3.5" />
              {r.verdict}
            </div>
          </div>
          <span className="font-mono text-xs text-muted">{r.on ? "On" : "Off"}</span>
        </li>
      ))}
    </ul>
  );
}

function Features() {
  return (
    <section id="features" aria-labelledby="features-title" className="scroll-mt-24 border-t border-hairline py-28 md:py-36">
      <div className="mx-auto max-w-6xl px-4 sm:px-6">
        <Reveal>
          <h2 id="features-title" className="max-w-[18ch] text-4xl leading-[1.08] font-medium tracking-[-0.025em] text-text md:text-5xl">
            Everything else it does, quietly
          </h2>
        </Reveal>
        <div className="mt-16 grid grid-cols-[minmax(0,1fr)] gap-5 md:grid-cols-6">
          <Reveal className="md:col-span-4">
            <CardSpotlight className="h-full">
              <div className="p-7">
                <Power aria-hidden="true" weight="light" className="size-6 text-text" />
                <h3 className="mt-5 text-xl font-medium text-text">Startup apps and services, with a verdict</h3>
                <p className="mt-2 max-w-[52ch] text-base leading-relaxed text-muted">
                  Every row says what it's for and what breaks if it's off. Protected rows say so instead of showing a switch.
                </p>
                <StartupRows />
              </div>
            </CardSpotlight>
          </Reveal>
          <Reveal className="md:col-span-2" delay={0.08}>
            <CardSpotlight className="h-full">
              <div className="flex h-full flex-col p-7">
                <Gauge aria-hidden="true" weight="light" className="size-6 text-text" />
                <h3 className="mt-5 text-xl font-medium text-text">Cleans on its own, sparingly</h3>
                <p className="mt-2 text-base leading-relaxed text-muted">
                  At a load you choose, from 50 to 99%. If memory stays full, the wait between cleans doubles.
                </p>
                <div className="mt-auto pt-8">
                  <div className="flex justify-between font-mono text-xs text-muted">
                    <span>50%</span>
                    <span className="text-text">85%</span>
                    <span>99%</span>
                  </div>
                  <div className="relative mt-2 h-1.5 rounded-full bg-surface-2 ring-1 ring-hairline">
                    <span className="absolute inset-y-0 left-0 w-[71%] rounded-full bg-inuse" />
                    <span aria-hidden="true" className="absolute top-1/2 left-[71%] size-4 -translate-1/2 rounded-full border-2 border-canvas bg-text" />
                  </div>
                </div>
              </div>
            </CardSpotlight>
          </Reveal>
          <Reveal className="md:col-span-2" delay={0.04}>
            <CardSpotlight className="h-full">
              <div className="p-7">
                <div aria-hidden="true" className="grid size-10 place-items-center rounded-lg border border-hairline-strong bg-surface-2 font-mono text-sm text-text">
                  71
                </div>
                <h3 className="mt-5 text-xl font-medium text-text">Load in the tray</h3>
                <p className="mt-2 text-base leading-relaxed text-muted">
                  The tray icon shows the current load. Hidden there, the app gives back the memory its own window used.
                </p>
              </div>
            </CardSpotlight>
          </Reveal>
          <Reveal className="md:col-span-4" delay={0.1}>
            <CardSpotlight className="h-full">
              <div className="p-7">
                <Terminal aria-hidden="true" weight="light" className="size-6 text-text" />
                <h3 className="mt-5 text-xl font-medium text-text">A command line too</h3>
                <p className="mt-2 max-w-[52ch] text-base leading-relaxed text-muted">
                  <code className="font-mono text-sm text-text">ramopt.exe</code> cleans, reports and watches from a script.
                  Exit codes say what failed.
                </p>
                <pre className="mt-6 overflow-x-auto rounded-xl border border-hairline bg-canvas p-4 font-mono text-[13px] leading-relaxed text-muted">
                  <code>
                    <span className="text-text">ramopt status</span>
                    {"\n"}
                    <span className="text-text">ramopt clean</span>
                    {"                   # default areas\n"}
                    <span className="text-text">ramopt watch --threshold 85</span>
                    {"  # clean at 85%"}
                  </code>
                </pre>
              </div>
            </CardSpotlight>
          </Reveal>
        </div>
      </div>
    </section>
  );
}

function Download() {
  return (
    <section id="download" aria-labelledby="download-title" className="relative isolate scroll-mt-24 overflow-hidden border-t border-hairline py-28 md:py-36">
      <div className="mx-auto grid max-w-6xl grid-cols-[minmax(0,1fr)] gap-14 px-4 sm:px-6 lg:grid-cols-[minmax(0,0.9fr)_minmax(0,1.1fr)] lg:gap-20">
        <Reveal>
          <h2 id="download-title" className="max-w-[12ch] text-4xl leading-[1.08] font-medium tracking-[-0.025em] text-text md:text-5xl">
            Download RamOptimiser
          </h2>
          <p className="mt-5 max-w-[40ch] text-lg leading-relaxed text-muted">
            One zip with the desktop app, the command line and the README. No installer, nothing to sign up for.
          </p>
          <div className="mt-9">
            <DownloadButton />
          </div>
          <dl className="mt-10 space-y-3 text-sm">
            {[
              ["Needs", "Windows 10 or 11, 64-bit, with .NET Framework 4.8 (built into Windows 10 May 2019 Update and later)"],
              ["To clean", "administrator rights; without them it shows status only"],
              ["File", `${release.file}, ${formatBytes(release.size)}`],
            ].map(([k, v]) => (
              <div key={k} className="grid grid-cols-[6rem_minmax(0,1fr)] gap-4 border-t border-hairline pt-3">
                <dt className="text-muted">{k}</dt>
                <dd className="text-text">{v}</dd>
              </div>
            ))}
          </dl>
        </Reveal>
        <Reveal delay={0.08}>
          <div className="rounded-[26px] border border-hairline bg-surface/50 p-1.5">
            <div className="rounded-[20px] border border-hairline/80 bg-surface p-7 shadow-[inset_0_1px_0_rgba(255,255,255,0.05)]">
              <h3 className="text-lg font-medium text-text">Check the file before you run it</h3>
              <p className="mt-2 text-base leading-relaxed text-muted">
                The app isn't code-signed yet, so Windows SmartScreen may warn the first time. Compare the hash below with
                the one PowerShell prints, then choose More info and Run anyway.
              </p>
              <h4 className="mt-6 text-sm font-medium text-muted">SHA-256</h4>
              <div className="mt-2">
                <CopyHash hash={release.sha256} />
              </div>
              <pre className="mt-2 overflow-x-auto rounded-xl border border-hairline bg-canvas p-4 font-mono text-[13px] text-text">
                <code>Get-FileHash .\{release.file}</code>
              </pre>
              <div className="mt-6 flex gap-3 rounded-xl border border-hairline bg-surface-2 p-4">
                <ShieldCheck aria-hidden="true" weight="light" className="mt-0.5 size-5 shrink-0 text-text" />
                <p className="text-sm leading-relaxed text-muted">
                  <span className="text-text">Before you turn on Start with Windows,</span> copy the two exes to a folder only
                  administrators can change, such as C:\Program Files\RamOptimiser. The start-up task runs the app as
                  administrator, so it shouldn't run from Downloads.
                </p>
              </div>
            </div>
          </div>
        </Reveal>
      </div>
    </section>
  );
}

export default function App() {
  return (
    <div className="grain min-h-dvh">
      <a
        href="#main"
        className="sr-only z-50 rounded-full bg-text px-4 py-2 text-canvas focus:not-sr-only focus:fixed focus:top-3 focus:left-3"
      >
        Skip to content
      </a>
      <Nav />
      <main id="main">
        <Hero />
        <How />
        <When />
        <Features />
        <Download />
      </main>
      <footer className="border-t border-hairline py-12">
        <div className="mx-auto flex max-w-6xl flex-col gap-3 px-4 text-sm text-muted sm:flex-row sm:justify-between sm:px-6">
          <p>RamOptimiser. An independent tool; its cleaning areas follow Mem Reduct's, with none of its code.</p>
          <p className="font-mono">Built {release.built}</p>
        </div>
      </footer>
    </div>
  );
}
