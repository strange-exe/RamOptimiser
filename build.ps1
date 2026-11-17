# Builds ramopt.exe (CLI), RamOptimiser.exe (GUI) and the test runner with the C# compiler that ships with
# Windows (.NET Framework 4.x). No SDK or download needed. Usage:  .\build.ps1        (build)
#                                                                  .\build.ps1 -Test  (build, then run the tests)
#                                                                  .\build.ps1 -Out x (write to folder x, e.g. while bin\ is running)
param([switch]$Test, [string]$Out)
$ErrorActionPreference = 'Stop'

# The real Windows folder (GetWindowsDirectory), not %WINDIR%, which the calling process can set to anything.
$fw = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc (.NET Framework 4.x x64 is required)." }

$root = $PSScriptRoot
$out = if ($Out) { [IO.Path]::GetFullPath((Join-Path $root $Out)) } else { Join-Path $root 'bin' }
$obj = Join-Path $out 'obj'
New-Item -ItemType Directory -Force $out, $obj | Out-Null
function Sources([string]$dir) { Get-ChildItem (Join-Path $root $dir) -Filter *.cs | ForEach-Object FullName }

$src = Sources 'src'           # shared core (Get-ChildItem without -Recurse does not see subfolders)
$gui = Sources 'src\Gui'       # GUI plumbing: settings, service, startup task, single instance, tray renderer
$logic = Sources 'src\App\Logic' # GUI wording and rules, free of WPF so the tests compile it too
$app = Sources 'src\App'       # WPF window, tray, theme
$tests = Sources 'tests'
$common = @('/nologo', '/platform:x64', '/optimize+', '/warnaserror+')
$drawing = @('/r:System.Drawing.dll', '/r:System.Windows.Forms.dll')

# 1. CLI
& $csc @common '/target:exe' "/out:$out\ramopt.exe" "/win32manifest:$root\src\app.manifest" @src
if ($LASTEXITCODE) { throw 'ramopt.exe build failed.' }

# 2. App icon, drawn in code at build time (16, 24, 32, 48 and 256 px).
& $csc @common '/target:exe' '/r:System.Drawing.dll' "/out:$obj\icongen.exe" "$root\tools\IconGen.cs" "$root\src\App\Logic\AppIcon.cs"
if ($LASTEXITCODE) { throw 'Icon generator build failed.' }
& "$obj\icongen.exe" "$obj\RamOptimiser.ico"
if ($LASTEXITCODE) { throw 'Icon generation failed.' }

# 3. GUI: WPF window from embedded XAML, WinForms NotifyIcon for the tray.
$wpf = Join-Path $fw 'WPF'
$wpfRefs = @("/r:$wpf\PresentationFramework.dll", "/r:$wpf\PresentationCore.dll", "/r:$wpf\WindowsBase.dll",
    "/r:$fw\System.Xaml.dll", "/r:$wpf\UIAutomationTypes.dll", "/r:$wpf\UIAutomationProvider.dll")
$xaml = Get-ChildItem (Join-Path $root 'src\App\Xaml') -Filter *.xaml | ForEach-Object {
    "/resource:$($_.FullName),RamOptimiser.Xaml.$($_.Name)"
}
& $csc @common '/target:winexe' @drawing @wpfRefs "/out:$out\RamOptimiser.exe" '/main:RamOptimiser.App.AppMain' `
    "/win32manifest:$root\src\App\app.manifest" "/win32icon:$obj\RamOptimiser.ico" `
    "/resource:$obj\RamOptimiser.ico,RamOptimiser.AppIcon.ico" @xaml @src @gui @logic @app
if ($LASTEXITCODE) { throw 'RamOptimiser.exe build failed.' }

# 4. Tests: the same sources minus the two entry points' UI, plus the test entry point.
& $csc @common '/target:exe' @drawing "/out:$out\ramopt-tests.exe" '/main:RamOptimiser.Tests.TestMain' @src @gui @logic @tests
if ($LASTEXITCODE) { throw 'Test build failed.' }

Write-Host "Built $out\ramopt.exe and $out\RamOptimiser.exe"
if ($Test) {
    & "$out\ramopt-tests.exe"
    exit $LASTEXITCODE
}
