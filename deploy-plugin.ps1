# Deploy an explicitly selected candidate while FM is closed.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$DllPath)
$ErrorActionPreference = 'Stop'
if (Get-Process fm -ErrorAction SilentlyContinue) { throw 'Save and close FM27 before deployment.' }
$sourcePath = (Resolve-Path -LiteralPath $DllPath -ErrorAction Stop).Path
$assembly = [Reflection.AssemblyName]::GetAssemblyName($sourcePath)
if ($assembly.Name -ne 'FMCoach') { throw "Expected an FMCoach assembly, got '$($assembly.Name)'." }
$targetPath = 'C:\Program Files (x86)\Steam\steamapps\common\Football Manager 27\BepInEx\plugins\FMCoach\FMCoach.dll'
$targetDirectory = Split-Path -Parent $targetPath
if (!(Test-Path -LiteralPath (Split-Path -Parent $targetDirectory))) { throw 'FM27 BepInEx plugins directory not found; install BepInEx first.' }
$sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
if (Get-Process fm -ErrorAction SilentlyContinue) { throw 'FM27 started during preparation; close it and retry.' }
Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force -ErrorAction Stop
$installedHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash
if ($installedHash -ne $sourceHash) { throw "Deployment hash mismatch. Source=$sourceHash Installed=$installedHash" }
Write-Host "FM Coach $($assembly.Version) deployed and SHA256 verified: $installedHash"
