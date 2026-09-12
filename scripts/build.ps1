[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$publishDirectory = Join-Path $artifactsDirectory 'publish'
$outputDirectory = Join-Path $repositoryRoot 'outputs'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
$localDotnet = Join-Path $repositoryRoot 'work\.dotnet\dotnet.exe'
if (-not $dotnet -or -not ((& $dotnet --list-sdks) -match '^10\.0\.')) {
    if (Test-Path -LiteralPath $localDotnet) { $dotnet = $localDotnet }
}
if (-not $dotnet) { throw '.NET SDK 10.0.401 or newer is required.' }

if (Test-Path -LiteralPath $artifactsDirectory) {
    $resolvedArtifacts = [IO.Path]::GetFullPath($artifactsDirectory)
    if (-not $resolvedArtifacts.StartsWith($repositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to clean an artifact directory outside the repository.'
    }
    Remove-Item -LiteralPath $resolvedArtifacts -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDirectory, $outputDirectory | Out-Null
$releaseFiles = @(
    'NexaGridSetup.exe',
    'SHA256SUMS.txt',
    'VERSION.txt',
    'CHANGELOG.md',
    'NexaGrid-Windows-x64.zip'
)
foreach ($releaseFile in $releaseFiles) {
    $releasePath = [IO.Path]::GetFullPath((Join-Path $outputDirectory $releaseFile))
    if (-not $releasePath.StartsWith($outputDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to clean a release file outside the output directory.'
    }
    if (Test-Path -LiteralPath $releasePath) { Remove-Item -LiteralPath $releasePath -Force }
}

& $dotnet restore (Join-Path $repositoryRoot 'NexaGrid.slnx')
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
& $dotnet build (Join-Path $repositoryRoot 'NexaGrid.slnx') -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$testProjects = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests') -Filter '*.csproj' -Recurse
foreach ($testProject in $testProjects) {
    $resultName = "$($testProject.BaseName).trx"
    & $dotnet test $testProject.FullName -c $Configuration --no-build --logger "trx;LogFileName=$resultName" --results-directory (Join-Path $artifactsDirectory 'test-results')
    if ($LASTEXITCODE -ne 0) { throw "Tests failed for $($testProject.BaseName)." }
}

$projects = @{
    controller = 'src\NexaGrid.Controller\NexaGrid.Controller.csproj'
    agent = 'src\NexaGrid.Agent\NexaGrid.Agent.csproj'
    simulator = 'src\NexaGrid.Simulator\NexaGrid.Simulator.csproj'
}
foreach ($item in $projects.GetEnumerator()) {
    & $dotnet publish (Join-Path $repositoryRoot $item.Value) -c $Configuration -r win-x64 --self-contained true -o (Join-Path $publishDirectory $item.Key) -p:PublishReadyToRun=true
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $($item.Key)." }
}

foreach ($executable in @(
    (Join-Path $publishDirectory 'controller\NexaGrid.Controller.exe'),
    (Join-Path $publishDirectory 'agent\NexaGrid.Agent.exe'),
    (Join-Path $publishDirectory 'simulator\NexaGrid.Simulator.exe')
)) {
    $header = [IO.File]::ReadAllBytes($executable)[0..1]
    if ($header[0] -ne 0x4D -or $header[1] -ne 0x5A) { throw "$executable is not a Windows PE executable." }
}

if (-not $SkipInstaller) {
    $compilerCandidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )
    $compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $compiler) { throw 'Inno Setup 6 is required to build the installer.' }
    & $compiler '/Qp' (Join-Path $repositoryRoot 'installer\NexaGrid.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $installer = Join-Path $outputDirectory 'NexaGridSetup.exe'
    $header = [IO.File]::ReadAllBytes($installer)[0..1]
    if ($header[0] -ne 0x4D -or $header[1] -ne 0x5A) { throw 'The generated installer is not a Windows PE executable.' }
}

[IO.File]::WriteAllText((Join-Path $outputDirectory 'VERSION.txt'), "0.1.0`r`n")
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'CHANGELOG.md') -Destination $outputDirectory -Force
$hashLines = @('CHANGELOG.md', 'NexaGridSetup.exe', 'VERSION.txt') |
    ForEach-Object {
        $releasePath = Join-Path $outputDirectory $_
        "{0}  {1}" -f (Get-FileHash -Algorithm SHA256 -LiteralPath $releasePath).Hash.ToLowerInvariant(), $_
    }
[IO.File]::WriteAllLines((Join-Path $outputDirectory 'SHA256SUMS.txt'), $hashLines)

$archive = Join-Path $outputDirectory 'NexaGrid-Windows-x64.zip'
Compress-Archive -Path (Join-Path $outputDirectory 'NexaGridSetup.exe'), (Join-Path $outputDirectory 'SHA256SUMS.txt'), (Join-Path $outputDirectory 'VERSION.txt'), (Join-Path $outputDirectory 'CHANGELOG.md') -DestinationPath $archive -CompressionLevel Optimal
Write-Output "Build complete: $outputDirectory"
