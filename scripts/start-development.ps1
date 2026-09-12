[CmdletBinding()]
param([int]$Port = 5187)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
if (-not $dotnet) {
    $dotnet = Join-Path $repositoryRoot 'work\.dotnet\dotnet.exe'
}
if (-not (Test-Path -LiteralPath $dotnet)) { throw '.NET SDK 10 is required.' }

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Controller__DataDirectory = Join-Path $repositoryRoot 'work\development-data'
$env:Controller__EnableHttpsListener = 'false'
$env:Controller__EnableLocalHttpListener = 'true'
$env:Controller__LocalHttpPort = [string]$Port

Write-Host "NexaGrid Control will be available at http://localhost:$Port"
& $dotnet run --project (Join-Path $repositoryRoot 'src\NexaGrid.Controller\NexaGrid.Controller.csproj')
