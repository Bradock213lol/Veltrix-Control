[CmdletBinding()]
param(
    [string]$InstallerPath = (Join-Path $PSScriptRoot '..\outputs\NexaGridSetup.exe'),
    [string]$InstallDirectory = (Join-Path $env:TEMP 'NexaGrid-Installer-Smoke')
)

$ErrorActionPreference = 'Stop'
$installer = [IO.Path]::GetFullPath($InstallerPath)
$installRoot = [IO.Path]::GetFullPath($InstallDirectory)
$temporaryRoots = @($env:TEMP, $env:RUNNER_TEMP) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar }
$productData = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'NexaGrid'))
$expectedProductData = [IO.Path]::GetFullPath('C:\ProgramData\NexaGrid')
$logPath = Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts'))) 'installer-smoke.log'
$startedAt = Get-Date

if (-not (Test-Path -LiteralPath $installer)) { throw "Installer not found: $installer" }
if (-not ($temporaryRoots | Where-Object { $installRoot.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })) {
    throw 'The smoke-test installation directory must be below the temporary directory.'
}
if ($productData -ne $expectedProductData) { throw "Unexpected product-data path: $productData" }
if (Test-Path -LiteralPath $productData) { throw "Refusing to overwrite existing NexaGrid data: $productData" }
if (Get-Service -Name 'NexaGridController', 'NexaGridAgent' -ErrorAction SilentlyContinue) {
    throw 'Refusing to replace an existing NexaGrid Windows service.'
}

$installerArguments = @(
    '/ROLE=both',
    '/VERYSILENT',
    '/SUPPRESSMSGBOXES',
    '/NORESTART',
    "/DIR=$installRoot",
    "/LOG=$logPath"
)

try {
    $setup = Start-Process -FilePath $installer -ArgumentList $installerArguments -Wait -PassThru -WindowStyle Hidden
    if ($setup.ExitCode -ne 0) { throw "Installer exited with code $($setup.ExitCode)." }

    foreach ($serviceName in @('NexaGridController', 'NexaGridAgent')) {
        $service = Get-Service -Name $serviceName
        $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
    }

    $healthy = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $health = Invoke-RestMethod -Uri 'http://localhost:5187/health' -TimeoutSec 2
            if ($health.status -eq 'healthy') { $healthy = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    if (-not $healthy) { throw 'The installed Controller did not become healthy.' }

    $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $password = "CI-$([Guid]::NewGuid().ToString('N'))"
    $setupBody = @{ username = 'ci.owner'; password = $password } | ConvertTo-Json
    Invoke-RestMethod -Uri 'http://localhost:5187/api/setup' -Method Post -ContentType 'application/json' -Body $setupBody -WebSession $session | Out-Null

    $deviceOnline = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $devices = @(Invoke-RestMethod -Uri 'http://localhost:5187/api/devices' -WebSession $session)
        if ($devices.Count -eq 1 -and $devices[0].online) { $deviceOnline = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $deviceOnline) { throw 'The combined-role Managed Node did not enroll and appear online.' }
    Write-Output "Installer smoke test passed: Controller healthy, Agent enrolled, $($devices.Count) node online."
}
catch {
    if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Tail 200 }
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $startedAt } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -like '*NexaGrid*' -or $_.Message -like '*NexaGrid*' } |
        Select-Object -First 50 TimeCreated, ProviderName, Id, LevelDisplayName, Message |
        Format-List
    throw
}
finally {
    $uninstaller = Join-Path $installRoot 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller) {
        $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru -WindowStyle Hidden
        if ($uninstall.ExitCode -ne 0) { Write-Warning "Uninstaller exited with code $($uninstall.ExitCode)." }
    }
    if (Test-Path -LiteralPath $productData) {
        $resolvedProductData = [IO.Path]::GetFullPath($productData)
        if ($resolvedProductData -ne $expectedProductData) { throw 'Refusing to clean an unexpected product-data path.' }
        Remove-Item -LiteralPath $resolvedProductData -Recurse -Force
        Write-Output "Removed installer smoke-test data: $resolvedProductData"
    }
}
