param(
  [Parameter(Mandatory=$true)][string]$ConfigPath,
  [switch]$Install
)
$ErrorActionPreference='Stop'
$config=Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$source=Join-Path $PSScriptRoot 'Watch-FactoryConnectStartup.ps1'
$destDir=Join-Path $config.installRoot 'deployment\startup'
$dest=Join-Path $destDir 'Watch-FactoryConnectStartup.ps1'
$configDest=Join-Path $destDir 'startup.config.json'
$taskName='FactoryConnect Startup Supervisor'
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Supervisor source missing' }
if ($config.enableRuntime -ne $false -and -not (Test-Path -LiteralPath (Join-Path $config.installRoot 'current\Start-FactoryConnectRuntime.ps1'))) { throw 'Runtime launcher missing' }
if (-not $Install) {
  Write-Host 'PREVIEW ONLY. No changes made.'
  Write-Host "Supervisor: $dest"
  Write-Host "Configuration: $configDest"
  Write-Host "Task: $taskName; SYSTEM; AtStartup; Highest"
  exit 0
}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator required' }
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { throw "Task '$taskName' exists; refusing to overwrite" }
$conflictingTasks=@(Get-ScheduledTask | Where-Object {
  $_.TaskName -match '(?i)factoryconnect.*(startup|supervisor)|(startup|supervisor).*factoryconnect' -or
  @($_.Actions | Where-Object { $_.Arguments -match '(?i)Watch-FactoryConnectStartup|Start-FactoryConnectSystem' }).Count -gt 0
})
if ($conflictingTasks.Count -gt 0) { throw "Existing FactoryConnect startup task(s) detected: $($conflictingTasks.TaskName -join ', '); refusing parallel supervisor installation" }
if (Test-Path -LiteralPath $dest) { throw "Existing supervisor at '$dest'; refusing to overwrite" }
if (Test-Path -LiteralPath $configDest) { throw "Existing configuration at '$configDest'; refusing to overwrite" }
if (-not $config.components) { throw 'No acquisition components configured; refusing installation' }
foreach ($component in $config.components) {
  if ($component.authorized -and ([string]::IsNullOrWhiteSpace([string]$component.executable) -or [string]::IsNullOrWhiteSpace([string]$component.matchArgument) -or [string]::IsNullOrWhiteSpace([string]$component.workingDirectory))) {
    throw "Authorized component '$($component.name)' lacks required process identity or working directory"
  }
}
New-Item -ItemType Directory -Path $destDir -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $dest -ErrorAction Stop
Copy-Item -LiteralPath $ConfigPath -Destination $configDest -ErrorAction Stop
$ps=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$arg='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -ConfigPath "{1}"' -f $dest,$configDest
$action=New-ScheduledTaskAction -Execute $ps -Argument $arg -WorkingDirectory $destDir
$trigger=New-ScheduledTaskTrigger -AtStartup
$principal=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'FactoryConnect unattended startup supervisor; installation-specific authorized acquisition' | Out-Null
Write-Host "Installed task: $taskName. Not started."
