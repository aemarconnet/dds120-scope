# Builds DDS120Scope.exe and the Dds120Probe diagnostics tool, then assembles the portable
# folder dist\DDS120Scope and dist\DDS120Scope-portable.zip.
#   powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

foreach ($proj in "src\DDS120Scope\DDS120Scope.csproj", "tools\Dds120Probe\Dds120Probe.csproj") {
    dotnet build (Join-Path $root $proj) -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $proj" }
}

$dist = Join-Path $root 'dist\DDS120Scope'
# Keep the settings of a copy that has been run from dist, but don't ship them in the zip.
$ini = Join-Path $dist 'DDS120Scope.ini'
$settings = if (Test-Path $ini) { [IO.File]::ReadAllBytes($ini) } else { $null }
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force (Join-Path $dist 'tools') | Out-Null

Copy-Item (Join-Path $root 'src\DDS120Scope\bin\Release\net48\DDS120Scope.exe') $dist
Copy-Item (Join-Path $root 'tools\Dds120Probe\bin\Release\net48\Dds120Probe.exe') (Join-Path $dist 'tools')
Copy-Item (Join-Path $root 'packaging\README.txt') $dist
Copy-Item (Join-Path $root 'packaging\FIRMWARE-LICENSE.txt') $dist

$zip = Join-Path $root 'dist\DDS120Scope-portable.zip'
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $dist -DestinationPath $zip
if ($settings) { [IO.File]::WriteAllBytes($ini, $settings) }

Get-ChildItem $dist -Recurse -File | ForEach-Object { '{0,10:N0}  {1}' -f $_.Length, $_.FullName.Substring($dist.Length + 1) }
'{0,10:N0}  {1}' -f (Get-Item $zip).Length, $zip
