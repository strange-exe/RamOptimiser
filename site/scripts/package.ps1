# Packages the built app for the website: zips the two exes, the README and the LICENSE, builds the installer
# (installer\RamOptimiser.iss) when Inno Setup 7 is found, writes each file's SHA-256 and size to src\release.json,
# and makes favicon.ico and a 180 px apple-touch-icon.png from the app's own icon.
# Run after `..\build.ps1 -Test`. Never runs the exes or the setup it builds.
# With -Repo owner/name the download links point at the GitHub Release assets (tag v<yyyy.MM.dd>), which is how
# the hosted site serves them; without it the files are served from the site itself (local preview).
# -Iscc names ISCC.exe; without it the usual Inno Setup 7 folders are tried. Without Inno Setup the zip is still
# made and release.json has no setup object, so the site offers the zip alone.
param([string]$Bin = (Join-Path $PSScriptRoot '..\..\bin'), [string]$Repo = '', [string]$Iscc = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$site = Resolve-Path (Join-Path $PSScriptRoot '..')
$root = (Resolve-Path (Join-Path $site '..')).Path
$bin = (Resolve-Path $Bin).Path
$files = @('RamOptimiser.exe', 'ramopt.exe') | ForEach-Object { Join-Path $bin $_ }
$files += Join-Path $root 'README.md'
$files += Join-Path $root 'LICENSE'
foreach ($f in $files) { if (-not (Test-Path $f)) { throw "missing $f; run build.ps1 -Test first" } }
if ($Repo -and $Repo -notmatch '^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$') { throw "Repo must look like owner/name" }

# The version build.ps1 compiled into the exes (yyyy.MM.dd), so the zip, the setup and the tag all match it.
$versionFile = Join-Path $bin 'version.txt'
if (-not (Test-Path $versionFile)) { throw "missing $versionFile; build with the current build.ps1, which writes it" }
$stamp = [IO.File]::ReadAllText($versionFile).Trim()
$built = [datetime]::MinValue
if ($stamp -notmatch '^\d{4}\.\d{2}\.\d{2}$' -or -not [datetime]::TryParseExact($stamp, 'yyyy.MM.dd',
        [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$built)) {
    throw "$versionFile must hold one date as yyyy.MM.dd"
}
function AssetUrl([string]$file) { if ($Repo) { "https://github.com/$Repo/releases/download/v$stamp/$file" } else { "/download/$file" } }

$name = "RamOptimiser-$stamp-win-x64.zip"
$out = Join-Path $site 'public\download'
New-Item -ItemType Directory -Force $out | Out-Null
Get-ChildItem $out -Filter 'RamOptimiser-*.zip' | Remove-Item
$zip = Join-Path $out $name
Compress-Archive -Path $files -DestinationPath $zip -CompressionLevel Optimal

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $zip).Length
$json = [ordered]@{ file = $name; url = (AssetUrl $name); tag = "v$stamp"; size = $size; sha256 = $hash; built = $built.ToString('yyyy-MM-dd') }

# The installer. The setup uses the build's icon, so check it before compiling.
$ico = Join-Path $bin 'obj\RamOptimiser.ico'
if (-not (Test-Path $ico)) { throw "missing $ico" }
if ($Iscc) {
    if (-not (Test-Path $Iscc -PathType Leaf)) { throw "ISCC.exe not found at $Iscc" }
} else {
    $Iscc = @(
        (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Inno Setup 7\ISCC.exe'),
        (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Inno Setup 7\ISCC.exe'),
        (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Inno Setup 7\ISCC.exe')
    ) | Where-Object { Test-Path $_ -PathType Leaf } | Select-Object -First 1
}
Get-ChildItem $out -Filter 'RamOptimiser-*-setup.exe' | Remove-Item
if ($Iscc) {
    $iss = Join-Path $root 'installer\RamOptimiser.iss'
    & $Iscc /Qp "/DAppVersion=$stamp" "/DBinDir=$bin" "/DRootDir=$root" "/O$out" $iss
    if ($LASTEXITCODE) { throw "ISCC.exe failed with exit code $LASTEXITCODE" }
    $setupName = "RamOptimiser-$stamp-setup.exe"
    $setup = Join-Path $out $setupName
    if (-not (Test-Path $setup)) { throw "ISCC.exe did not write $setup" }
    $json.setup = [ordered]@{ file = $setupName; url = (AssetUrl $setupName); size = (Get-Item $setup).Length
        sha256 = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant() }
} else {
    Write-Warning "Inno Setup 7 (ISCC.exe) not found: no installer built. release.json lists the zip only, so the site offers the zip."
}

$text = (($json | ConvertTo-Json) -replace "`r`n", "`n") + "`n"
[IO.File]::WriteAllText((Join-Path $site 'src\release.json'), $text, [Text.UTF8Encoding]::new($false))

# Icons from the app icon the build makes.
Copy-Item $ico (Join-Path $site 'public\favicon.ico') -Force
$icon = New-Object System.Drawing.Icon($ico, 256, 256)
$src = $icon.ToBitmap()
$png = New-Object System.Drawing.Bitmap(180, 180)
$g = [System.Drawing.Graphics]::FromImage($png)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.Clear([System.Drawing.Color]::FromArgb(255, 8, 8, 10))
$g.DrawImage($src, 14, 14, 152, 152)
$png.Save((Join-Path $site 'public\apple-touch-icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $png.Dispose(); $src.Dispose(); $icon.Dispose()

"packaged $name ($size bytes) sha256 $hash"
if ($json.setup) { "packaged $($json.setup.file) ($($json.setup.size) bytes) sha256 $($json.setup.sha256)" }
