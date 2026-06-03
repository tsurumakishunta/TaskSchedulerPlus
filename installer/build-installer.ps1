#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"

$installerDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $installerDir
$publishRoot = Join-Path $root "artifacts\installer\TaskSchedulerPlus"
$zipPath = Join-Path $root "artifacts\installer\TaskSchedulerPlus-Installer-$Runtime.zip"
$webProject = Join-Path $root "src\TaskSchedulerPlus.Web\TaskSchedulerPlus.Web.csproj"
$workerProject = Join-Path $root "src\TaskSchedulerPlus.Worker\TaskSchedulerPlus.Worker.csproj"
$webOutput = Join-Path $publishRoot "Web"
$workerOutput = Join-Path $publishRoot "Worker"

if (Test-Path -LiteralPath $publishRoot) {
    $resolved = (Resolve-Path -LiteralPath $publishRoot).Path
    if (-not $resolved.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove outside workspace: $resolved"
    }

    Remove-Item -LiteralPath $resolved -Recurse -Force
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

$selfContained = if ($FrameworkDependent) { "false" } else { "true" }

Write-Host "Publishing Web..."
dotnet publish $webProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained $selfContained `
    -o $webOutput
if ($LASTEXITCODE -ne 0) {
    throw "Web publish failed. ExitCode=$LASTEXITCODE"
}

Write-Host "Publishing Worker..."
dotnet publish $workerProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained $selfContained `
    -p:ErrorOnDuplicatePublishOutputFiles=false `
    -o $workerOutput
if ($LASTEXITCODE -ne 0) {
    throw "Worker publish failed. ExitCode=$LASTEXITCODE"
}

Copy-Item -LiteralPath (Join-Path $root "src\TaskSchedulerPlus.Web\appsettings.json") -Destination (Join-Path $webOutput "appsettings.json") -Force
Copy-Item -LiteralPath (Join-Path $root "src\TaskSchedulerPlus.Web\appsettings.Production.json") -Destination (Join-Path $webOutput "appsettings.Production.json") -Force
Copy-Item -LiteralPath (Join-Path $root "src\TaskSchedulerPlus.Worker\appsettings.json") -Destination (Join-Path $workerOutput "appsettings.json") -Force
Copy-Item -LiteralPath (Join-Path $root "src\TaskSchedulerPlus.Worker\appsettings.Production.json") -Destination (Join-Path $workerOutput "appsettings.Production.json") -Force

New-Item -ItemType Directory -Path (Join-Path $publishRoot "Parameter") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $publishRoot "App_Data") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $webOutput "logs") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $workerOutput "logs") -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $installerDir "install.ps1") -Destination (Join-Path $publishRoot "install.ps1") -Force
Copy-Item -LiteralPath (Join-Path $installerDir "uninstall.ps1") -Destination (Join-Path $publishRoot "uninstall.ps1") -Force
Copy-Item -LiteralPath (Join-Path $root "docs\INSTALLER.md") -Destination (Join-Path $publishRoot "README_INSTALLER.md") -Force

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

dotnet build-server shutdown | Out-Null
Start-Sleep -Seconds 1

$archiveCreated = $false
for ($attempt = 1; $attempt -le 3; $attempt++) {
    try {
        Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $zipPath -Force
        $archiveCreated = $true
        break
    }
    catch {
        if ($attempt -eq 3) {
            throw
        }

        Start-Sleep -Seconds 2
    }
}

if (-not $archiveCreated) {
    throw "Archive creation failed."
}

Write-Host "Installer package created."
Write-Host $zipPath
