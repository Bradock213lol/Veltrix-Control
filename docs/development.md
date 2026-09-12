# Development guide

## Prerequisites

- Windows 10/11 or Windows Server x64
- .NET 10 SDK 10.0.401 or a compatible patch
- PowerShell 7 recommended
- Inno Setup 6 for installer builds

## Run locally

`scripts/start-development.ps1` stores disposable state under `work/`, disables the
remote HTTPS listener, and serves the dashboard on loopback. Complete first-owner
setup in the browser, create an enrollment code, then run a simulator:

```powershell
dotnet run --project src/NexaGrid.Simulator -- --token CODE --controller http://localhost:5187 --name Simulated-PC-01
```

The simulator uses the production signature/replay protocol but never performs a real
restart or shutdown. Stop it with Ctrl+C; the Controller marks it offline after the
configured heartbeat window.

## Verification

```powershell
dotnet restore NexaGrid.slnx
dotnet build NexaGrid.slnx -c Release --no-restore
dotnet test NexaGrid.slnx -c Release --no-build
```

`scripts/build.ps1` also publishes and packages. Tests use a unique temporary SQLite
database per Controller factory and clear connection pools before cleanup.

## Configuration

Controller settings are under `Controller`; Agent settings are under `Agent`. Use
environment variables with double underscores for deployment overrides. Never commit
enrollment codes, private keys, production database paths, or certificate files.

The real Agent refuses non-HTTPS Controller URLs except loopback with the explicit
`AllowInsecureLoopback` development switch. Real power actions are separately disabled
by default with `AllowPowerActions=false`.
