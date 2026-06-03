#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$InstallRoot = "C:\Program Files\TaskSchedulerPlus",
    [string]$WebServiceName = "TaskSchedulerPlusWeb",
    [string]$WorkerServiceName = "TaskSchedulerPlusWorker",
    [switch]$RemoveData
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

if (-not (Test-Administrator)) {
    throw "Administrator privileges are required. Run PowerShell as administrator."
}

$targetRoot = [IO.Path]::GetFullPath($InstallRoot)

Write-Host "Uninstalling Task Scheduler Plus."
Stop-And-RemoveService -Name $WorkerServiceName
Stop-And-RemoveService -Name $WebServiceName

if ($RemoveData -and (Test-Path -LiteralPath $targetRoot)) {
    $resolved = (Resolve-Path -LiteralPath $targetRoot).Path
    if ($resolved -eq [IO.Path]::GetPathRoot($resolved)) {
        throw "Refusing to remove root directory: $resolved"
    }

    Remove-Item -LiteralPath $resolved -Recurse -Force
    Write-Host "Install directory removed: $resolved"
}
else {
    Write-Host "Services removed. Data and logs were preserved: $targetRoot"
    Write-Host "Run with -RemoveData to remove the install directory."
}
