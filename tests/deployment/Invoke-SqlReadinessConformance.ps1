[CmdletBinding()]
param([switch]$LiveSql,[string]$ProviderExecutable)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../../scripts/deployment/FactoryConnect.SqlReadiness.ps1')
if (-not [string]::IsNullOrWhiteSpace($ProviderExecutable)) { $script:SqlReadinessExecutable = $ProviderExecutable }
$root = Join-Path ([IO.Path]::GetTempPath()) ('FactoryConnect-SqlReadiness-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $root 'config') -Force | Out-Null
$results=[ordered]@{}
function Assert-True([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Set-Config([string]$Name,[string]$ConnectionString,[string]$Provider='SqlServer') {
    @{Persistence=@{Provider=$Provider};PersistenceProviders=@{SqlServer=@{ConnectionString=$ConnectionString}}} |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $root "config/$Name.production.json")
}
function Reset-Config {
    Set-Config edge 'Server=edge-test;Database=commissioned;Integrated Security=True'
    Set-Config api 'Server=api-test;Database=commissioned;Integrated Security=True'
    $script:Captured=$null
}
function Capture-Evidence($Value) { $script:Captured=$Value }
function Case([string]$Name,[scriptblock]$Body) { Reset-Config; & $Body; $results[$Name]='PASS'; Write-Host "$Name PASS" }
try {
    Case BothAuthenticatedTargetsRequired {
        $seen=New-Object 'Collections.Generic.List[string]'
        Wait-FactoryConnectSqlReadiness $root -TimeoutSeconds 2 -Probe {
            param($connection,$budget,$token)
            $seen.Add($connection)
            Assert-True ($budget -gt 0 -and $budget -le 2000) 'Probe exceeded remaining budget.'
            return $true
        } -OnEvidence ${function:Capture-Evidence}
        Assert-True ($seen.Count -eq 2 -and $seen[0].Contains('edge-test') -and $seen[1].Contains('api-test')) 'Both commissioned targets were not checked.'
        Assert-True ($script:Captured.status -eq 'Ready') 'Success evidence absent.'
    }
    Case DelayedAvailability {
        $script:Calls=0
        Wait-FactoryConnectSqlReadiness $root -TimeoutSeconds 2 -RetryMilliseconds 10 -Probe {
            $script:Calls++
            return ($script:Calls -gt 2)
        } -OnEvidence ${function:Capture-Evidence}
        Assert-True ($script:Captured.attempts -eq 2) 'Delayed SQL did not retry.'
        Assert-True ($script:Captured.targets.edge -eq 'Ready' -and $script:Captured.targets.api -eq 'Ready') 'Readiness did not include both targets.'
    }
    Case PermanentFailureAndRedaction {
        $caught=$false
        try {
            Wait-FactoryConnectSqlReadiness $root -TimeoutSeconds 1 -RetryMilliseconds 10 -Probe {
                throw 'Server=secret;Password=DO-NOT-LOG'
            } -OnEvidence ${function:Capture-Evidence}
        } catch {
            $caught=$true
            Assert-True (-not $_.Exception.Message.Contains('DO-NOT-LOG')) 'Provider error leaked.'
        }
        Assert-True $caught 'Unavailable SQL was accepted.'
        Assert-True ($script:Captured.status -eq 'TimedOut') 'Timeout evidence absent.'
        Assert-True ($script:Captured.elapsedMilliseconds -lt 2500) 'Shared timeout was not bounded.'
        Assert-True (-not ($script:Captured | ConvertTo-Json -Depth 5).Contains('secret')) 'Evidence leaked connection data.'
    }
    Case SuccessCannotAccumulateAcrossPasses {
        $script:Calls=0
        $caught=$false
        try {
            Wait-FactoryConnectSqlReadiness $root -TimeoutSeconds 1 -RetryMilliseconds 10 -Probe {
                $script:Calls++
                $pass=[int][Math]::Ceiling($script:Calls / 2.0)
                return (($pass % 2 -eq 1 -and $script:Calls % 2 -eq 1) -or
                        ($pass % 2 -eq 0 -and $script:Calls % 2 -eq 0))
            } -OnEvidence ${function:Capture-Evidence}
        } catch { $caught=$true }
        Assert-True ($caught -and $script:Captured.status -eq 'TimedOut') 'Successes from different passes were combined.'
    }
    Case InvalidApiFailsBeforeAnyContact {
        Set-Config api 'Password=DO-NOT-LOG;InvalidKeyword=x'
        $script:Calls=0; $caught=$false
        try {
            Wait-FactoryConnectSqlReadiness $root -Probe { $script:Calls++; return $true } -OnEvidence ${function:Capture-Evidence}
        } catch { $caught=$true; Assert-True (-not $_.Exception.Message.Contains('DO-NOT-LOG')) 'Parser error leaked.' }
        Assert-True ($caught -and $script:Calls -eq 0) 'Invalid config made SQL contact.'
        Assert-True ($script:Captured.status -eq 'ConfigurationInvalid') 'Configuration failure evidence absent.'
    }
    Case MissingDatabaseFailsClosed {
        Set-Config edge 'Server=test;Integrated Security=True'
        $caught=$false
        try { Wait-FactoryConnectSqlReadiness $root } catch { $caught=$true }
        Assert-True $caught 'Unspecified database accepted.'
    }
    Case PlaceholderFailsClosed {
        Set-Config api 'Server=__SQL_SERVER__;Database=FactoryConnect;Integrated Security=True'
        $caught=$false
        try { Wait-FactoryConnectSqlReadiness $root } catch { $caught=$true }
        Assert-True $caught 'Placeholder accepted.'
    }
    Case WrongProviderFailsClosed {
        Set-Config edge 'Server=test;Database=FactoryConnect;Integrated Security=True' 'InMemory'
        $caught=$false
        try { Wait-FactoryConnectSqlReadiness $root } catch { $caught=$true }
        Assert-True $caught 'Wrong provider accepted.'
    }
    Case MissingConfigurationFailsClosed {
        Remove-Item (Join-Path $root 'config/api.production.json')
        $caught=$false
        try { Wait-FactoryConnectSqlReadiness $root } catch { $caught=$true }
        Assert-True $caught 'Missing API configuration accepted.'
    }
    Case CancellationDuringRetry {
        $cts=New-Object Threading.CancellationTokenSource
        try {
            $cts.CancelAfter(100)
            $caught=$false
            try {
                Wait-FactoryConnectSqlReadiness $root -TimeoutSeconds 10 -RetryMilliseconds 5000 -CancellationToken $cts.Token -Probe { return $false } -OnEvidence ${function:Capture-Evidence}
            } catch { $caught=$true }
            Assert-True ($caught -and $script:Captured.status -eq 'Cancelled') 'Cancellation was not recorded.'
            Assert-True ($script:Captured.elapsedMilliseconds -lt 2000) 'Retry ignored cancellation.'
        } finally { $cts.Dispose() }
    }
    Case ConfigurationBytesPreserved {
        $before=@((Get-FileHash (Join-Path $root 'config/edge.production.json')).Hash,(Get-FileHash (Join-Path $root 'config/api.production.json')).Hash)
        Wait-FactoryConnectSqlReadiness $root -Probe { return $true }
        Assert-True ($before[0] -eq (Get-FileHash (Join-Path $root 'config/edge.production.json')).Hash) 'Edge configuration changed.'
        Assert-True ($before[1] -eq (Get-FileHash (Join-Path $root 'config/api.production.json')).Hash) 'API configuration changed.'
    }
    Case AuthenticationRejectionFailsClosed {
        $caught=$false
        try {
            Wait-FactoryConnectSqlReadiness $root -TimeoutSeconds 10 -Probe {
                throw 'SQL readiness authentication rejected; runtime not invoked.'
            } -OnEvidence ${function:Capture-Evidence}
        } catch { $caught=$true }
        Assert-True ($caught -and $script:Captured.status -eq 'AuthenticationRejected') 'Permanent authentication rejection was retried or accepted.'
        Assert-True ($script:Captured.attempts -eq 1) 'Authentication rejection did not stop the first pass.'
    }
    Case MissingPackagedProviderFailsClosed {
        $saved=$script:SqlReadinessExecutable
        try {
            $script:SqlReadinessExecutable=Join-Path $root 'absent.exe'
            $caught=$false
            try { Wait-FactoryConnectSqlReadiness $root -OnEvidence ${function:Capture-Evidence} } catch { $caught=$true }
            Assert-True ($caught -and $script:Captured.status -eq 'ConfigurationInvalid') 'Missing packaged provider did not fail closed.'
            Assert-True ($script:Captured.attempts -eq 0) 'Missing provider attempted readiness.'
        } finally { $script:SqlReadinessExecutable=$saved }
    }
    if ($LiveSql) {
        Case LiveAuthenticatedSelect {
            $connection=[Environment]::GetEnvironmentVariable('FACTORYCONNECT_SQL_READINESS_TEST_CONNECTION_STRING')
            Assert-True (-not [string]::IsNullOrWhiteSpace($connection)) 'Live SQL test requires its dedicated connection environment variable.'
            Set-Config edge $connection
            Set-Config api $connection
            Wait-FactoryConnectSqlReadiness $root -TimeoutSeconds 30 -OnEvidence ${function:Capture-Evidence}
            Assert-True ($script:Captured.status -eq 'Ready') 'Live authenticated SELECT failed.'
            Set-Config api ($connection + ';Initial Catalog=FactoryConnect_Readiness_Missing_' + [Guid]::NewGuid().ToString('N'))
            $caught=$false
            try { Wait-FactoryConnectSqlReadiness $root -TimeoutSeconds 2 -RetryMilliseconds 20 -AttemptMilliseconds 500 -OnEvidence ${function:Capture-Evidence} } catch { $caught=$true }
            Assert-True ($caught -and $script:Captured.status -eq 'TimedOut') 'Nonexistent database passed readiness.'
        }
    }
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
[pscustomobject]$results
