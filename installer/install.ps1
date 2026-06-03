#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$InstallRoot = "C:\Program Files\TaskSchedulerPlus",
    [string]$WebServiceName = "TaskSchedulerPlusWeb",
    [string]$WorkerServiceName = "TaskSchedulerPlusWorker",
    [string]$WebUrl = "http://localhost:7255",
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Stop-And-RemoveService {
    param([string]$Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        return
    }

    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $Name -Force
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }

    sc.exe delete $Name | Out-Null
    Start-Sleep -Seconds 2
}

function Enable-ServiceRecovery {
    param([string]$Name)

    sc.exe failure $Name reset= 60 actions= restart/60000/restart/60000/""/60000 | Out-Null
}

function Copy-Directory {
    param(
        [string]$Source,
        [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        throw "Source directory was not found: $Source"
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    robocopy $Source $Destination /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -gt 7) {
        throw "Copy failed. Source=$Source Destination=$Destination ExitCode=$LASTEXITCODE"
    }
}

if (-not (Test-Administrator)) {
    throw "Administrator privileges are required. Run PowerShell as administrator."
}

$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceWeb = Join-Path $packageRoot "Web"
$sourceWorker = Join-Path $packageRoot "Worker"
$targetRoot = [IO.Path]::GetFullPath($InstallRoot)
$targetWeb = Join-Path $targetRoot "Web"
$targetWorker = Join-Path $targetRoot "Worker"
$targetParameter = Join-Path $targetRoot "Parameter"
$targetAppData = Join-Path $targetRoot "App_Data"
$targetWebLogs = Join-Path $targetWeb "logs"
$targetWorkerLogs = Join-Path $targetWorker "logs"

Write-Host "Installing Task Scheduler Plus."
Write-Host "InstallRoot: $targetRoot"

Stop-And-RemoveService -Name $WorkerServiceName
Stop-And-RemoveService -Name $WebServiceName

New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null
Copy-Directory -Source $sourceWeb -Destination $targetWeb
Copy-Directory -Source $sourceWorker -Destination $targetWorker

New-Item -ItemType Directory -Path $targetParameter -Force | Out-Null
New-Item -ItemType Directory -Path $targetAppData -Force | Out-Null
New-Item -ItemType Directory -Path $targetWebLogs -Force | Out-Null
New-Item -ItemType Directory -Path $targetWorkerLogs -Force | Out-Null

$defaultMailPath = Join-Path $targetParameter "mail.json"
if (-not (Test-Path -LiteralPath $defaultMailPath)) {
    @'
{
  "Enabled": false,
  "SmtpHost": "",
  "SmtpPort": 587,
  "EnableSsl": true,
  "SenderAddress": "",
  "SenderName": "Task Scheduler Plus",
  "UserName": "",
  "Password": "",
  "TimeoutSeconds": 30
}
'@ | Set-Content -Path $defaultMailPath -Encoding UTF8
}

Copy-Item -LiteralPath (Join-Path $packageRoot "install.ps1") -Destination (Join-Path $targetRoot "install.ps1") -Force
Copy-Item -LiteralPath (Join-Path $packageRoot "uninstall.ps1") -Destination (Join-Path $targetRoot "uninstall.ps1") -Force

$webExe = Join-Path $targetWeb "TaskSchedulerPlus.Web.exe"
$workerExe = Join-Path $targetWorker "TaskSchedulerPlus.Worker.exe"

if (-not (Test-Path -LiteralPath $webExe)) {
    throw "Web executable was not found: $webExe"
}

if (-not (Test-Path -LiteralPath $workerExe)) {
    throw "Worker executable was not found: $workerExe"
}

New-Service `
    -Name $WebServiceName `
    -DisplayName "Task Scheduler Plus Web" `
    -Description "Provides the Task Scheduler Plus web management UI." `
    -BinaryPathName "`"$webExe`" --urls `"$WebUrl`"" `
    -StartupType Automatic | Out-Null

New-Service `
    -Name $WorkerServiceName `
    -DisplayName "Task Scheduler Plus Worker" `
    -Description "Runs schedule monitoring and task execution for Task Scheduler Plus." `
    -BinaryPathName "`"$workerExe`"" `
    -StartupType Automatic | Out-Null

Enable-ServiceRecovery -Name $WebServiceName
Enable-ServiceRecovery -Name $WorkerServiceName

if (-not $NoStart) {
    Start-Service -Name $WebServiceName
    Start-Service -Name $WorkerServiceName
}

Write-Host "Install completed."
Write-Host "WebUrl: $WebUrl"
Write-Host "ParameterPath: $targetParameter"
Write-Host "UninstallScript: $targetRoot\uninstall.ps1"
