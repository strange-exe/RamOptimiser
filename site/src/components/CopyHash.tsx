import { Check, Copy } from "@phosphor-icons/react";
import { useState } from "react";

// The full SHA-256, selectable, with a copy button that confirms in words (announced politely).
export function CopyHash({ hash, label = "Copy SHA-256" }: { hash: string; label?: string }) {
  const [state, setState] = useState<"idle" | "copied" | "failed">("idle");
  async function copy() {
    try {
      await navigator.clipboard.writeText(hash);
      setState("copied");
    } catch {
      setState("failed");
    }
    window.setTimeout(() => setState("idle"), 2400);
  }
  return (
    <div>
      <div className="flex items-start gap-3">
        <code className="min-w-0 flex-1 break-all rounded-xl border border-hairline bg-canvas px-4 py-3 font-mono text-[13px] leading-relaxed text-text select-all">
          {hash}
        </code>
        <button
          type="button"
          onClick={copy}
          aria-label={label}
          className="grid size-11 shrink-0 place-items-center rounded-xl border border-hairline-strong text-text transition-[transform,background-color] duration-300 ease-[cubic-bezier(0.22,1,0.36,1)] hover:bg-surface-2 active:scale-95"
        >
          {state === "copied" ? <Check weight="light" className="size-5 text-ok" /> : <Copy weight="light" className="size-5" />}
        </button>
      </div>
      <p aria-live="polite" className="mt-2 min-h-5 text-sm text-muted">
        {state === "copied" ? "Copied" : state === "failed" ? "Couldn't copy. Select the hash and copy it yourself." : ""}
      </p>
    </div>
  );
}
