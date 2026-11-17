# Packages the built app for the website: zips the two exes and the README from ..\bin, writes the SHA-256 and
# size to src\release.json, and makes favicon.ico and a 180 px apple-touch-icon.png from the app's own icon.
# Run after `..\build.ps1 -Test`. Reads only; never runs the exes.
# With -Repo owner/name the download link points at the GitHub Release asset (tag v<yyyy.MM.dd>), which is how
# the hosted site serves it; without it the zip is served from the site itself (local preview).
param([string]$Bin = (Join-Path $PSScriptRoot '..\..\bin'), [string]$Repo = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$site = Resolve-Path (Join-Path $PSScriptRoot '..')
$bin = Resolve-Path $Bin
$files = @('RamOptimiser.exe', 'ramopt.exe') | ForEach-Object { Join-Path $bin $_ }
$files += Join-Path $site '..\README.md'
foreach ($f in $files) { if (-not (Test-Path $f)) { throw "missing $f; run build.ps1 -Test first" } }

$built = (Get-Item $files[0]).LastWriteTimeUtc
$stamp = $built.ToString('yyyy.MM.dd')
$name = "RamOptimiser-$stamp-win-x64.zip"
$out = Join-Path $site 'public\download'
New-Item -ItemType Directory -Force $out | Out-Null
Get-ChildItem $out -Filter 'RamOptimiser-*.zip' | Remove-Item
$zip = Join-Path $out $name
Compress-Archive -Path $files -DestinationPath $zip -CompressionLevel Optimal

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $zip).Length
if ($Repo -and $Repo -notmatch '^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$') { throw "Repo must look like owner/name" }
$url = if ($Repo) { "https://github.com/$Repo/releases/download/v$stamp/$name" } else { "/download/$name" }
$json = [ordered]@{ file = $name; url = $url; tag = "v$stamp"; size = $size; sha256 = $hash; built = $built.ToString('yyyy-MM-dd') }
$text = (($json | ConvertTo-Json) -replace "`r`n", "`n") + "`n"
[IO.File]::WriteAllText((Join-Path $site 'src\release.json'), $text, [Text.UTF8Encoding]::new($false))

# Icons from the app icon the build makes.
$ico = Join-Path $bin 'obj\RamOptimiser.ico'
if (-not (Test-Path $ico)) { throw "missing $ico" }
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
