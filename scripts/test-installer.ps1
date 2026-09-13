[CmdletBinding()]
param(
    [string]$InstallerPath = (Join-Path $PSScriptRoot '..\outputs\Veltrix-Control-Setup.exe'),
    [string]$InstallDirectory = (Join-Path $env:TEMP 'Veltrix-Control-Installer-Smoke')
)

$ErrorActionPreference = 'Stop'
$installer = [IO.Path]::GetFullPath($InstallerPath)
$installRoot = [IO.Path]::GetFullPath($InstallDirectory)
$temporaryRoots = @($env:TEMP, $env:RUNNER_TEMP) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar }
$productData = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'Veltrix-Control'))
$expectedProductData = [IO.Path]::GetFullPath('C:\ProgramData\Veltrix-Control')
$logPath = Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts'))) 'installer-smoke.log'
$startedAt = Get-Date

if (-not (Test-Path -LiteralPath $installer)) { throw "Installer not found: $installer" }
if (-not ($temporaryRoots | Where-Object { $installRoot.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })) {
    throw 'The smoke-test installation directory must be below the temporary directory.'
}
if ($productData -ne $expectedProductData) { throw "Unexpected product-data path: $productData" }
if (Test-Path -LiteralPath $productData) { throw "Refusing to overwrite existing Veltrix-Control data: $productData" }
if (Get-Service -Name 'Veltrix-Control-Controller', 'Veltrix-Control-Agent' -ErrorAction SilentlyContinue) {
    throw 'Refusing to replace an existing Veltrix-Control Windows service.'
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

    foreach ($application in @(
        (Join-Path $installRoot 'Desktop\Veltrix-Control.Desktop.exe'),
        (Join-Path $installRoot 'Launcher\Veltrix-Control.exe')
    )) {
        if (-not (Test-Path -LiteralPath $application)) { throw "Installed application is missing: $application" }
        $header = [IO.File]::ReadAllBytes($application)[0..1]
        if ($header[0] -ne 0x4D -or $header[1] -ne 0x5A) { throw "Installed application is not a Windows executable: $application" }
    }

    foreach ($serviceName in @('Veltrix-Control-Controller', 'Veltrix-Control-Agent')) {
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

    $repair = Start-Process -FilePath $installer -ArgumentList $installerArguments -Wait -PassThru -WindowStyle Hidden
    if ($repair.ExitCode -ne 0) { throw "Installer repair exited with code $($repair.ExitCode)." }
    foreach ($serviceName in @('Veltrix-Control-Controller', 'Veltrix-Control-Agent')) {
        $service = Get-Service -Name $serviceName
        $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
    }
    $healthyAfterRepair = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $health = Invoke-RestMethod -Uri 'http://localhost:5187/health' -TimeoutSec 2
            if ($health.status -eq 'healthy') { $healthyAfterRepair = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    if (-not $healthyAfterRepair) { throw 'The repaired Controller did not become healthy.' }

    $repairSession = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $loginBody = @{ username = 'ci.owner'; password = $password } | ConvertTo-Json
    Invoke-RestMethod -Uri 'http://localhost:5187/api/auth/login' -Method Post -ContentType 'application/json' -Body $loginBody -WebSession $repairSession | Out-Null
    $devicesAfterRepair = @(Invoke-RestMethod -Uri 'http://localhost:5187/api/devices' -WebSession $repairSession)
    if ($devicesAfterRepair.Count -ne 1 -or -not $devicesAfterRepair[0].online) {
        throw 'Repair did not preserve the enrolled online node.'
    }
    Write-Output "Installer smoke test passed: clean install, services, enrollment, repair, state preservation, and $($devicesAfterRepair.Count) node online."
}
catch {
    if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Tail 200 }
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $startedAt } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -like '*Veltrix-Control*' -or $_.Message -like '*Veltrix-Control*' } |
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
