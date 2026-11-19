import { DownloadSimple } from "@phosphor-icons/react";
import { formatBytes } from "../lib/utils";
import { primary, release } from "../lib/release";

// Primary action: off-white pill on obsidian, with the icon in its own nested circle. It fetches the installer
// when the release has one, else the zip.
export function DownloadButton({ className = "" }: { className?: string }) {
  return (
    <a
      href={primary.url}
      download
      className={`group inline-flex min-h-12 items-center gap-4 rounded-full bg-text py-1.5 pr-1.5 pl-6 text-[15px] font-semibold text-canvas transition-transform duration-300 ease-[cubic-bezier(0.22,1,0.36,1)] active:scale-[0.97] ${className}`}
    >
      Download for Windows
      <span className="sr-only">
        , {release.setup ? "installer" : "zip file"}, {formatBytes(primary.size)}
      </span>
      <span
        aria-hidden="true"
        className="grid size-9 place-items-center rounded-full bg-canvas/10 transition-transform duration-500 ease-[cubic-bezier(0.34,1.4,0.64,1)] group-hover:translate-y-0.5 group-hover:scale-105"
      >
        <DownloadSimple weight="bold" className="size-4" />
      </span>
    </a>
  );
}

// The secondary option once there is an installer: the same two exes in a zip, for people who don't install
// software. Renders nothing when the zip is already the main button.
export function PortableZipLink({ className = "" }: { className?: string }) {
  if (!release.setup) return null;
  return (
    <a
      href={release.url}
      download
      className={`inline-flex min-h-6 items-center text-sm text-muted underline decoration-hairline-strong underline-offset-4 transition-colors hover:text-text hover:decoration-text ${className}`}
    >
      Portable zip, {formatBytes(release.size)}
    </a>
  );
}
