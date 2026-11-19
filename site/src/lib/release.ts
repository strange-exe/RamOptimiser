import data from "../release.json";

// site/scripts/package.ps1 writes release.json: the portable zip's fields at the top level and, when Inno Setup
// was found at packaging time, a setup object for the installer. Without one the page offers the zip alone.
export interface Asset {
  file: string;
  url: string;
  size: number;
  sha256: string;
}

export interface Release extends Asset {
  tag: string;
  built: string;
  setup?: Asset;
}

export const release: Release = data;

// What the main download button fetches: the installer when there is one, else the zip.
export const primary: Asset = release.setup ?? release;
