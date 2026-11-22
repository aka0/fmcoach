# Offline regression checks. Does not start FM, deploy a plugin, or contact an API.
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$outputDir = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$dotnetRoot = Split-Path (Get-Command dotnet).Source
$compiler = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'Roslyn/bincore/csc.dll' } |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$runtime = Get-ChildItem (Join-Path $dotnetRoot 'shared/Microsoft.NETCore.App') -Directory |
    Where-Object { $_.Name -like '6.0.*' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$compiler -or !$runtime) { throw 'Requires a .NET SDK and the .NET 6 runtime used by BepInEx.' }

function Get-References($files) {
    foreach ($file in $files) {
        try { [Reflection.AssemblyName]::GetAssemblyName($file.FullName) | Out-Null }
        catch { continue } # Runtime directories also contain native DLLs.
        '/reference:"' + $file.FullName + '"'
    }
}
$frameworkRefs = @(Get-References (Get-ChildItem (Join-Path $runtime.FullName '*.dll')))
$pluginRefs = @(Get-References @((Get-ChildItem (Join-Path $projectRoot 'ref/*.dll')) +
    (Get-ChildItem (Join-Path $projectRoot 'interop/*.dll'))))

function Invoke-Compiler($name, $arguments) {
    $response = Join-Path $outputDir ($name + '.rsp')
    Set-Content -LiteralPath $response -Value (@('/nologo', '/langversion:latest', '/unsafe') + $arguments)
    & dotnet $compiler ('@' + $response)
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $name" }
}
foreach ($tree in @('src')) {
    $sourceDir = Join-Path $projectRoot $tree
    # Verify references relative to the actual project, independent of the compiler harness.
    [xml]$project = Get-Content (Join-Path $sourceDir 'FMCoach.csproj')
    foreach ($reference in $project.Project.ItemGroup.Reference) {
        if ($reference.Include -and !(Test-Path (Join-Path $sourceDir $reference.Include))) {
            throw "Missing project reference: $($reference.Include) in $tree"
        }
    }
    $pluginOutput = Join-Path $outputDir ($tree + '.dll')
    $sourceFiles = @(Get-ChildItem (Join-Path $sourceDir '*.cs') | ForEach-Object { '"' + $_.FullName + '"' })
    Invoke-Compiler ($tree + '-plugin') (@('/target:library', ('/out:"' + $pluginOutput + '"')) +
        $frameworkRefs + $pluginRefs + $sourceFiles + @(
            ('/resource:"' + (Join-Path $sourceDir 'coach_system_prompt.txt') + '",FMCoach.coach_system_prompt.txt'),
            ('/resource:"' + (Join-Path $sourceDir 'coach_faces_32x40.rgba') + '",FMCoach.coach_faces_32x40.rgba')))

    $testOutput = Join-Path $outputDir ($tree + '-regression.dll')
    $testSources = @((Join-Path $PSScriptRoot 'Regression.cs')) + @('CoachSession.cs', 'CoachChat.cs', 'CoachTacticSnapshot.cs', 'CoachFacePixels.cs', 'CoachSquadPolicy.cs', 'CoachSnapshotCoverage.cs', 'CoachCoroutine.cs', 'CoachInputText.cs', 'CoachReadableText.cs', 'CoachTeamStats.cs', 'CoachQualityProfile.cs', 'CoachOpponentAdvice.cs' |
        ForEach-Object { Join-Path $sourceDir $_ })
    Invoke-Compiler ($tree + '-regression') (@('/target:exe', '/nowarn:0169,0649,0414', ('/out:"' + $testOutput + '"')) +
        $frameworkRefs + @($testSources | ForEach-Object { '"' + $_ + '"' }))
    $runtimeConfig = @{ runtimeOptions = @{ tfm = 'net6.0'; framework = @{ name = 'Microsoft.NETCore.App'; version = $runtime.Name } } }
    $runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ($testOutput -replace '\.dll$', '.runtimeconfig.json')
    $mode = 'latest'
    & dotnet $testOutput $mode $sourceDir
    if ($LASTEXITCODE -ne 0) { throw "Regression checks failed: $tree" }
}
