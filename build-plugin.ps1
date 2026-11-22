# Build an immutable, version-stamped candidate. Does not install or start FM.
[CmdletBinding()]
param(
    [string]$SourceDirectory = (Join-Path $PSScriptRoot 'src'),
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
$sourceDir = [IO.Path]::GetFullPath($SourceDirectory)
if (!$sourceDir.StartsWith($workspace, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source must be inside this workspace.' }
$pluginFile = Join-Path $sourceDir 'Plugin.cs'
$match = [regex]::Match((Get-Content -LiteralPath $pluginFile -Raw), '\[BepInPlugin\([^\r\n]*"(\d+\.\d+\.\d+)"\)\]')
if (!$match.Success) { throw 'Cannot determine BepInPlugin version.' }
$version = $match.Groups[1].Value
$projectFile = Join-Path $sourceDir 'FMCoach.csproj'
$projectVersion = [regex]::Match((Get-Content -LiteralPath $projectFile -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
if ($projectVersion -ne $version) { throw "Version mismatch: BepInPlugin=$version, FMCoach.csproj=$projectVersion. Update both before building." }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot ('builds/v' + $version + '/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')) }
$outputDir = [IO.Path]::GetFullPath($OutputDirectory)
if (!$outputDir.StartsWith($workspace, [StringComparison]::OrdinalIgnoreCase)) { throw 'Build output must be inside this workspace.' }
if (Test-Path -LiteralPath $outputDir) { throw "Build output already exists; choose a new directory: $outputDir" }

$dotnetRoot = Split-Path (Get-Command dotnet -ErrorAction Stop).Source
$compiler = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'Roslyn/bincore/csc.dll' } |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$runtime = Get-ChildItem (Join-Path $dotnetRoot 'shared/Microsoft.NETCore.App') -Directory |
    Where-Object { $_.Name -match '^6\.0\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$compiler -or !$runtime) { throw 'An installed .NET SDK and .NET 6 runtime are required.' }

$codeFiles = @(Get-ChildItem -LiteralPath $sourceDir -Filter '*.cs' -File | Sort-Object Name)
$resourceFiles = @('coach_system_prompt.txt', 'coach_faces_32x40.rgba' | ForEach-Object { Get-Item -LiteralPath (Join-Path $sourceDir $_) })
$inputs = @($codeFiles) + @($resourceFiles) + @(Get-Item -LiteralPath (Join-Path $sourceDir 'FMCoach.csproj'))
$inputHashes = @{}
foreach ($file in $inputs) { $inputHashes[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName).Hash }

[xml]$project = Get-Content -LiteralPath (Join-Path $sourceDir 'FMCoach.csproj')
foreach ($reference in $project.Project.ItemGroup.Reference) {
    if ($reference.Include -and !(Test-Path (Join-Path $sourceDir $reference.Include))) { throw "Missing project reference: $($reference.Include)" }
}
$referenceFiles = @((Get-ChildItem (Join-Path $runtime.FullName '*.dll'))) +
    @((Get-ChildItem (Join-Path $PSScriptRoot 'ref/*.dll'))) + @((Get-ChildItem (Join-Path $PSScriptRoot 'interop/*.dll')))
$references = @()
foreach ($file in $referenceFiles) {
    try { [Reflection.AssemblyName]::GetAssemblyName($file.FullName) | Out-Null } catch { continue }
    $references += '/reference:"' + $file.FullName + '"'
}

New-Item -ItemType Directory -Path $outputDir | Out-Null
$assemblyInfo = Join-Path $outputDir 'BuildAssemblyInfo.cs'
$stamp = @"
using System.Reflection;
using System.Runtime.Versioning;
[assembly: AssemblyVersion("$version.0")]
[assembly: AssemblyFileVersion("$version.0")]
[assembly: AssemblyInformationalVersion("$version")]
[assembly: AssemblyTitle("FM Coach")]
[assembly: TargetFramework(".NETCoreApp,Version=v6.0", FrameworkDisplayName = ".NET 6.0")]
"@
[IO.File]::WriteAllText($assemblyInfo, $stamp, (New-Object Text.UTF8Encoding $false))
$dll = Join-Path $outputDir 'FMCoach.dll'
$arguments = @('/nologo', '/target:library', '/langversion:latest', '/unsafe', '/optimize+', '/deterministic+', ('/out:"' + $dll + '"')) +
    $references + @($codeFiles | ForEach-Object { '"' + $_.FullName + '"' }) + @('"' + $assemblyInfo + '"' -join '') + @(
        ('/resource:"' + $resourceFiles[0].FullName + '",FMCoach.coach_system_prompt.txt'),
        ('/resource:"' + $resourceFiles[1].FullName + '",FMCoach.coach_faces_32x40.rgba'))
$response = Join-Path $outputDir 'build.rsp'
[IO.File]::WriteAllLines($response, [string[]]$arguments, (New-Object Text.UTF8Encoding $false))
& dotnet $compiler ('@' + $response) | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw "Plugin compilation failed: $outputDir" }
foreach ($file in $inputs) {
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne $inputHashes[$file.FullName]) { throw "Source changed during compilation: $($file.Name). Discard this build." }
}
$assembly = [Reflection.AssemblyName]::GetAssemblyName($dll)
$fileInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
if ($assembly.Name -ne 'FMCoach' -or $assembly.Version.ToString() -ne "$version.0" -or $fileInfo.FileVersion -ne "$version.0") {
    throw 'Built DLL assembly/file metadata does not match BepInPlugin version.'
}
$manifest = [ordered]@{
    version = $version; assemblyVersion = $assembly.Version.ToString(); fileVersion = $fileInfo.FileVersion
    dll = $dll; sha256 = (Get-FileHash -LiteralPath $dll).Hash
    sourceDirectory = $sourceDir; inputHashes = $inputHashes
    compiler = $compiler; runtime = $runtime.Name; createdUtc = [DateTime]::UtcNow.ToString('o')
}
[IO.File]::WriteAllText((Join-Path $outputDir 'build.json'), ($manifest | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
[pscustomobject]$manifest
