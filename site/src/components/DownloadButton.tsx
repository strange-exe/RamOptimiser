import { DownloadSimple } from "@phosphor-icons/react";
import release from "../release.json";
import { formatBytes } from "../lib/utils";

// Primary action: off-white pill on obsidian, with the icon in its own nested circle.
export function DownloadButton({ className = "" }: { className?: string }) {
  return (
    <a
      href={release.url}
      download
      className={`group inline-flex min-h-12 items-center gap-4 rounded-full bg-text py-1.5 pr-1.5 pl-6 text-[15px] font-semibold text-canvas transition-transform duration-300 ease-[cubic-bezier(0.22,1,0.36,1)] active:scale-[0.97] ${className}`}
    >
      Download for Windows
      <span className="sr-only">, zip file, {formatBytes(release.size)}</span>
      <span
        aria-hidden="true"
        className="grid size-9 place-items-center rounded-full bg-canvas/10 transition-transform duration-500 ease-[cubic-bezier(0.34,1.4,0.64,1)] group-hover:translate-y-0.5 group-hover:scale-105"
      >
        <DownloadSimple weight="bold" className="size-4" />
      </span>
    </a>
  );
}
