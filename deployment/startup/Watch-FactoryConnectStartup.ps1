param(
  [Parameter(Mandatory=$true)][string]$ConfigPath,
  [switch]$Once,
  [ValidateRange(15,3600)][int]$IntervalSeconds=30
)
$ErrorActionPreference='Stop'
$config=Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$logDir=Join-Path $config.installRoot 'deployment\logs\startup'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$logPath=Join-Path $logDir 'watch.log'
function Log([string]$message) {
  $line='{0} {1}' -f (Get-Date -Format o),$message
  Add-Content -LiteralPath $logPath -Value $line
  Write-Output $line
}
function Find-Processes([string]$exe) {
  $name=[IO.Path]::GetFileName($exe)
  @(Get-CimInstance Win32_Process -Filter "Name='$name'" | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath.Equals($exe,[StringComparison]::OrdinalIgnoreCase)
  })
}
function Check-Http([string]$url) {
  try { (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200 }
  catch { $false }
}
function Start-ConfiguredComponent($c) {
  if (-not $c.authorized) { return }
  if (-not (Test-Path -LiteralPath $c.executable -PathType Leaf)) { Log "ERROR $($c.name) executable missing"; return }
  if (-not (Test-Path -LiteralPath $c.workingDirectory -PathType Container)) { Log "ERROR $($c.name) working directory missing"; return }
  if ([string]::IsNullOrWhiteSpace([string]$c.matchArgument)) { Log "ERROR $($c.name) matchArgument is required"; return }
  $found=@(Find-Processes $c.executable)
  $matching=@($found | Where-Object { $_.CommandLine -and $_.CommandLine.Contains([string]$c.matchArgument) })
  if ($matching.Count -gt 1) { Log "WARNING $($c.name) duplicate matching processes detected"; return }
  if ($matching.Count -eq 1) { return }
  if ($found.Count -gt 0 -and [IO.Path]::GetFileName($c.executable) -ine 'powershell.exe') { Log "WARNING $($c.name) executable already running with different arguments; refusing duplicate"; return }
  Log "START $($c.name)"
  try {
    Start-Process -FilePath $c.executable -ArgumentList ([string]$c.arguments) -WorkingDirectory $c.workingDirectory -WindowStyle Hidden -ErrorAction Stop
  } catch { Log "ERROR $($c.name) start failed: $($_.Exception.Message)" }
}
function Cycle {
  try {
    foreach ($c in @($config.components)) { Start-ConfiguredComponent $c }
    foreach ($url in @($config.acquisitionHealthUrls)) {
      if (-not (Check-Http $url)) { Log "WARNING acquisition endpoint unavailable: $url" }
    }
    $sql=Get-Service -Name $config.sqlService -ErrorAction SilentlyContinue
    if (-not $sql -or $sql.Status -ne 'Running') { Log 'WAIT SQL service not running'; return }
    $names=@('FactoryConnect.Edge.exe','FactoryConnect.Api.exe','FactoryConnect.Dashboard.exe')
    $running=@($names | Where-Object { @(Get-CimInstance Win32_Process -Filter "Name='$_'").Count -gt 0 })
    if ($running.Count -eq 0) {
      $launcher=Join-Path $config.installRoot 'current\Start-FactoryConnectRuntime.ps1'
      if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) { Log 'ERROR runtime launcher missing'; return }
      Log 'START FactoryConnect runtime BootSafe'
      $ps=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
      $args='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -InstallRoot "{1}" -StartupMode BootSafe' -f $launcher,$config.installRoot
      $p=Start-Process -FilePath $ps -ArgumentList $args -WorkingDirectory $config.installRoot -PassThru -Wait -WindowStyle Hidden
      Log "Runtime launcher exit code $($p.ExitCode)"
    } elseif ($running.Count -ne 3) {
      Log 'WARNING partial runtime; manual investigation required, no forced restart'
    }
    foreach ($url in @($config.runtimeHealthUrls)) {
      if (-not (Check-Http $url)) { Log "WARNING runtime endpoint unavailable: $url" }
    }
  } catch { Log "ERROR cycle: $($_.Exception.Message)" }
}
Log 'FactoryConnect startup supervisor launched'
do {
  Cycle
  if ($Once) { break }
  Start-Sleep -Seconds $IntervalSeconds
} while ($true)
