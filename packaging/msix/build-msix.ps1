# Builds the Microsoft Store package (MSIX) of the Windows app into dist\msix\.
#   powershell -ExecutionPolicy Bypass -File packaging\msix\build-msix.ps1
#
# The package is unsigned: the Store signs it after certification. Its identity comes from
# store.json, which must hold the values Partner Center shows under Product management >
# Product identity. Until it does, a "-localtest" package with a placeholder identity is built
# instead, which the Store rejects.
#
# Needs makeappx.exe and makepri.exe from the Windows SDK, or from the
# Microsoft.Windows.SDK.BuildTools NuGet package that Visual Studio keeps.
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Find-Tool([string]$name) {
    $places = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Shared\NuGetPackages\microsoft.windows.sdk.buildtools",
        "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools")
    $found = foreach ($p in $places) {
        if (Test-Path $p) { Get-ChildItem $p -Recurse -Filter $name -ErrorAction SilentlyContinue | Where-Object { $_.DirectoryName -match '\\x64$' } }
    }
    $tool = $found | Sort-Object { [version]($_.VersionInfo.FileVersion -replace '[^\d.].*$', '') } -Descending | Select-Object -First 1
    if (-not $tool) { throw "$name not found: install the Windows SDK (or Visual Studio's Windows app tools)." }
    return $tool.FullName
}

function Xml([string]$s) { return [System.Security.SecurityElement]::Escape($s) }

$makeappx = Find-Tool 'makeappx.exe'
$makepri = Find-Tool 'makepri.exe'

# Version: the app's <Version> plus a fourth part, which the Store requires to be 0.
[xml]$proj = Get-Content (Join-Path $root 'src\DDS120Scope\DDS120Scope.csproj')
$appVersion = @($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
$version = "$appVersion.0"

$cfg = Get-Content (Join-Path $PSScriptRoot 'store.json') -Raw | ConvertFrom-Json
$forStore = [bool]($cfg.identityName -and $cfg.publisher -and $cfg.publisherDisplayName)
if ($forStore) {
    $identity = $cfg.identityName; $publisher = $cfg.publisher; $publisherName = $cfg.publisherDisplayName; $suffix = ''
} else {
    Write-Warning 'store.json has no Partner Center identity yet: building a LOCAL TEST package. The Store will reject it.'
    $identity = 'DDS120Scope.LocalTest'; $publisher = 'CN=DDS120Scope Local Test'; $publisherName = 'Local test'; $suffix = '-localtest'
}

if (-not $SkipBuild) {
    foreach ($p in 'src\DDS120Scope\DDS120Scope.csproj', 'tools\Dds120Probe\Dds120Probe.csproj') {
        dotnet build (Join-Path $root $p) -c Release -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $p" }
    }
}

# Package layout
$work = Join-Path $root 'obj\msix'
$layout = Join-Path $work 'layout'
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $layout | Out-Null
Copy-Item (Join-Path $root 'src\DDS120Scope\bin\Release\net48\DDS120Scope.exe') $layout
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $layout 'LICENSE.txt')
Copy-Item (Join-Path $root 'packaging\GPL-2.0.txt') $layout
Copy-Item (Join-Path $root 'packaging\FIRMWARE-LICENSE.txt') $layout
Copy-Item (Join-Path $root 'packaging\store\EULA.txt') $layout

$probe = Join-Path $root 'tools\Dds120Probe\bin\Release\net48\Dds120Probe.exe'
& $probe storeassets (Join-Path $layout 'Assets') (Join-Path $root 'dist\store')
if ($LASTEXITCODE -ne 0) { throw 'Rendering the images failed' }

$manifest = Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
$manifest = $manifest.Replace('$IDENTITY_NAME$', (Xml $identity)).Replace('$PUBLISHER$', (Xml $publisher)).
    Replace('$PUBLISHER_DISPLAY_NAME$', (Xml $publisherName)).Replace('$DISPLAY_NAME$', (Xml $cfg.displayName)).
    Replace('$VERSION$', $version)
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding $false))

# resources.pri lets Windows pick the right image size (scale-*, targetsize-*).
$priConfig = Join-Path $work 'priconfig.xml'
& $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makepri createconfig failed' }
# One package, so keep every image size in the one resources.pri instead of per-scale files
# meant for split resource packages.
[xml]$pc = Get-Content $priConfig
if ($pc.resources.packaging) { [void]$pc.resources.RemoveChild($pc.resources.packaging) }
$pc.Save($priConfig)
& $makepri new /pr $layout /cf $priConfig /mn (Join-Path $layout 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makepri new failed' }

$outDir = Join-Path $root 'dist\msix'
New-Item -ItemType Directory -Force $outDir | Out-Null
$out = Join-Path $outDir "DDS120Scope-$version$suffix.msix"
& $makeappx pack /d $layout /p $out /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makeappx pack failed' }

'{0:N0} bytes  {1}' -f (Get-Item $out).Length, $out
if ($forStore) { 'Upload this file in Partner Center under Packages.' }
