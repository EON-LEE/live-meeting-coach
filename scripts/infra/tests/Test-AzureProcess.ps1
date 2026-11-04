[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $scriptsRoot 'AzureProcess.psm1') -Force
$script:passed = 0
function Assert-True {
    param([bool]$Condition, [string]$Name)
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:passed++
    Write-Output "PASS: $Name"
}
$hostPath = (Get-Process -Id $PID).Path
$fixture = Join-Path $PSScriptRoot 'fixtures\cli fixture.ps1'
$prefix = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $fixture)
$result = Invoke-BoundedJsonCommand -Path $hostPath -Prefix ($prefix + @('-Mode', 'arguments')) -Arguments @('argument with spaces', 'semi;colon', 'amp&ersand') -TimeoutSeconds 10
Assert-True ($result.Succeeded -and $result.Data.arguments.Count -eq 3 -and
    $result.Data.arguments[0] -ceq 'argument with spaces' -and $result.Data.arguments[1] -ceq 'semi;colon' -and
    $result.Data.arguments[2] -ceq 'amp&ersand') 'Executable and prefix/argument arrays preserve boundaries without shell injection'
$result = Invoke-BoundedJsonCommand -Path 'missing-cli-fixture.exe' -TimeoutSeconds 1
Assert-True (-not $result.Succeeded -and $result.Code -eq 'TOOL_UNAVAILABLE') 'Missing CLI returns explicit unavailable result'
$result = Invoke-BoundedJsonCommand -Path $hostPath -Prefix ($prefix + @('-Mode', 'invalid')) -TimeoutSeconds 10
Assert-True (-not $result.Succeeded -and $result.Code -eq 'INVALID_JSON' -and
    ($result | ConvertTo-Json) -notmatch 'PRIVATE_DIAGNOSTIC_SENTINEL') 'Malformed CLI output is redacted, not echoed'
$result = Invoke-BoundedJsonCommand -Path $hostPath -Prefix ($prefix + @('-Mode', 'failure')) -TimeoutSeconds 10
Assert-True (-not $result.Succeeded -and $result.Code -eq 'COMMAND_FAILED' -and
    ($result | ConvertTo-Json) -notmatch 'PRIVATE_DIAGNOSTIC_SENTINEL') 'Nonzero CLI stderr is redacted and cannot masquerade as success'
$watch = [Diagnostics.Stopwatch]::StartNew()
$result = Invoke-BoundedJsonCommand -Path $hostPath -Prefix ($prefix + @('-Mode', 'timeout')) -TimeoutSeconds 2
$watch.Stop()
Assert-True (-not $result.Succeeded -and $result.Code -eq 'TIMEOUT' -and $watch.Elapsed.TotalSeconds -lt 10) 'Hung CLI is killed by PID tree within a bounded timeout'
$result = Invoke-BoundedJsonCommand -Path $hostPath -Prefix ($prefix + @('-Mode', 'no-auth')) -TimeoutSeconds 10
Assert-True ($result.Succeeded -and @($result.Data | Where-Object { $null -ne $_ }).Count -eq 0) 'Empty account list is preserved as no identity'

$releaseFixture = Join-Path $PSScriptRoot 'fixtures\release.fixture.json'
$approved = Get-Content -LiteralPath $releaseFixture -Raw | ConvertFrom-Json
$previousReleaseConfig = $env:VOICE_ASSISTANT_RELEASE_CONFIG
$modulePath = $env:PSModulePath
$oldLog = $env:VOICE_INFRA_FIXTURE_LOG
$oldMode = $env:VOICE_INFRA_FIXTURE_MODE
$log = [IO.Path]::GetTempFileName()
$templateFile = [IO.Path]::GetTempFileName()
$parameterFile = [IO.Path]::GetTempFileName()
try {
    $env:VOICE_INFRA_FIXTURE_LOG = $log
    $env:VOICE_ASSISTANT_RELEASE_CONFIG = $releaseFixture
    $env:PSModulePath = (Join-Path $PSScriptRoot 'fixtures') + [IO.Path]::PathSeparator + $modulePath
    $env:VOICE_INFRA_FIXTURE_MODE = ''
    '{}' | Set-Content -LiteralPath $templateFile
    '{"parameters":{}}' | Set-Content -LiteralPath $parameterFile
    $sub = @('--subscription', $approved.subscriptionId)
    $r = Invoke-AzPowerShellCommand (@('account', 'show') + $sub) 15
    Assert-True ($r.Succeeded -and $r.Data.state -eq 'Enabled' -and (Get-Item $log).Length -gt 0) 'Az provider uses isolated module fixture and preserves target account'
    $env:VOICE_ASSISTANT_RELEASE_CONFIG = Join-Path $PSScriptRoot 'fixtures\absent-release.local.json'
    $before = (Get-Item $log).Length
    $r = Invoke-AzPowerShellCommand (@('account', 'show') + $sub) 15
    Assert-True (-not $r.Succeeded -and (Get-Item $log).Length -eq $before) 'Az provider fails closed without the local approved target, before any request'
    $env:VOICE_ASSISTANT_RELEASE_CONFIG = $releaseFixture
    $r = Invoke-AzPowerShellCommand (@('group', 'exists', '--name', 'fixture-missing') + $sub) 15
    Assert-True ($r.Succeeded -and $r.Data -eq $false) 'Az provider distinguishes a real 404 from access errors'
    $r = Invoke-AzPowerShellCommand (@('provider', 'show', '--namespace', 'Microsoft.ContainerRegistry') + $sub) 15
    Assert-True ($r.Succeeded -and $r.Data -eq 'Registered') 'Az provider returns registration status without global context changes'
    $tags = @('--tags', 'managedBy=voice-assistant-bootstrap', "projectId=$($approved.projectId)")
    $r = Invoke-AzPowerShellCommand (@('group', 'create', '--name', 'fixture-meeting-group', '--location', 'eastus') + $tags + $sub) 15
    Assert-True ($r.Succeeded -and $r.Data.tags.managedBy -eq 'voice-assistant-bootstrap') 'Az provider creates exact tagged group using fixture ARM PUT'
    $r = Invoke-AzPowerShellCommand (@('acr', 'create', '--name', 'fixturemeetingacr', '--resource-group', 'fixture-meeting-group',
        '--location', 'eastus', '--sku', 'Basic') + $tags + $sub) 15
    $entries = @(Get-Content -LiteralPath $log | ForEach-Object { $_ | ConvertFrom-Json })
    $registryPut = @($entries | Where-Object { $_.method -eq 'PUT' -and $_.path -match '/registries/' })[0]
    $registryBody = $registryPut.payload | ConvertFrom-Json
    Assert-True ($r.Succeeded -and $registryBody.sku.name -eq 'Basic' -and $registryBody.properties.adminUserEnabled -eq $false -and
        $registryBody.properties.roleAssignmentMode -eq 'LegacyRegistryPermissions') 'Az registry PUT disables admin credentials and explicitly selects RBAC mode'
    $r = Invoke-AzPowerShellCommand (@('acr', 'list', '--resource-group', 'fixture-meeting-group') + $sub) 15
    Assert-True ($r.Succeeded -and @($r.Data)[0].roleAssignmentMode -eq 'LegacyRegistryPermissions') 'Az registry reads preserve authorization-mode evidence'
    $deployment = @('--name', 'fixture-deployment', '--resource-group', 'fixture-meeting-group', '--mode', 'Incremental',
        '--template-file', $templateFile, '--parameters', ('@' + $parameterFile)) + $sub
    $r = Invoke-AzPowerShellCommand (@('deployment', 'group', 'validate') + $deployment) 15
    Assert-True ($r.Succeeded -and $r.Data.properties.provisioningState -eq 'Succeeded') 'Az deployment validation maps explicit compiled template and parameters'
    $r = Invoke-AzPowerShellCommand (@('deployment', 'group', 'what-if') + $deployment) 15
    Assert-True ($r.Succeeded -and $r.Data.changes[0].changeType -eq 'Create') 'Az what-if follows a bounded subscription-scoped asynchronous result'
    Assert-True (($r.Data | ConvertTo-Json -Depth 10) -notmatch 'PRIVATE_DIFF_SENTINEL|correlationId|properties') 'Nested ARM properties.changes normalizes to CLI-shaped summaries without configuration diffs'
    $entries = @(Get-Content -LiteralPath $log | ForEach-Object { $_ | ConvertFrom-Json })
    $poll = @($entries | Where-Object { $_.method -eq 'GET' -and $_.path -match '/operationResults/' })[-1]
    Assert-True ($poll.path.EndsWith('&sig=PRIVATE_POLL_SENTINEL%2B%2f%3D') -and
        ($r | ConvertTo-Json -Depth 10) -notmatch 'PRIVATE_POLL_SENTINEL') 'Enumerable header values preserve signed query exactly without exposing polling URL'
    $env:VOICE_INFRA_FIXTURE_MODE = 'header-dictionary'
    $r = Invoke-AzPowerShellCommand (@('deployment', 'group', 'what-if') + $deployment) 15
    Assert-True ($r.Succeeded -and $r.Data.changes[0].changeType -eq 'Create') 'Dictionary Location header remains supported'
    foreach ($mode in @('header-duplicate-values', 'header-duplicate-entries', 'header-missing', 'header-bad-host')) {
        $env:VOICE_INFRA_FIXTURE_MODE = $mode
        $r = Invoke-AzPowerShellCommand (@('deployment', 'group', 'what-if') + $deployment) 15
        Assert-True (-not $r.Succeeded -and ($r | ConvertTo-Json -Depth 10) -notmatch 'PRIVATE_POLL_SENTINEL') "Invalid Location header fails closed without URL disclosure: $mode"
    }
    $env:VOICE_INFRA_FIXTURE_MODE = 'whatif-empty-changes'
    $r = Invoke-AzPowerShellCommand (@('deployment', 'group', 'what-if') + $deployment) 15
    Assert-True ($r.Succeeded -and $r.Data.PSObject.Properties['changes'] -and @($r.Data.changes).Count -eq 0) 'Successful empty ARM changes remain an explicit empty CLI-shaped array'
    $env:VOICE_INFRA_FIXTURE_MODE = 'whatif-missing-changes'
    $r = Invoke-AzPowerShellCommand (@('deployment', 'group', 'what-if') + $deployment) 15
    Assert-True (-not $r.Succeeded) 'Missing ARM changes are rejected rather than reported as a successful empty preview'
    $env:VOICE_INFRA_FIXTURE_MODE = ''
    $r = Invoke-AzPowerShellCommand (@('deployment', 'group', 'create') + $deployment) 15
    Assert-True ($r.Succeeded -and $r.Data.properties.provisioningState -eq 'Succeeded') 'Az deployment create polls until terminal ARM status'
    $r = Invoke-AzPowerShellCommand @('group', 'create', '--name', 'fixture-meeting-group', '--subscription', '11111111-1111-4111-8111-111111111111') 15
    Assert-True (-not $r.Succeeded) 'Az provider rejects a different subscription before executing any request'
    $env:VOICE_INFRA_FIXTURE_MODE = 'failure'
    $r = Invoke-AzPowerShellCommand (@('group', 'exists', '--name', 'fixture-missing') + $sub) 15
    Assert-True (-not $r.Succeeded -and ($r | ConvertTo-Json -Depth 10) -notmatch 'PRIVATE_DIAGNOSTIC_SENTINEL') 'Az provider exceptions remain explicit failures without raw provider content'
    $env:VOICE_INFRA_FIXTURE_MODE = ''
    foreach ($operation in @(
        @{ args = @('search', 'service', 'show', '--name', 'fixture-search'); expectedName = 'fixture-search' },
        @{ args = @('cognitiveservices', 'account', 'show', '--name', 'fixture-ai'); expectedName = 'fixture-ai' },
        @{ args = @('storage', 'account', 'show', '--name', 'fixturestorage'); expectedName = 'fixturestorage' }
    )) {
        $r = Invoke-AzPowerShellCommand ($operation.args + @('--resource-group', 'fixture-meeting-group') + $sub) 15
        Assert-True ($r.Succeeded -and $r.Data.name -eq $operation.expectedName -and
            $r.Data.PSObject.Properties['properties']) "Az ingestion metadata preserves ARM name/properties shape: $($operation.expectedName)"
    }
    $tokenArguments = @('account', 'get-access-token', '--tenant', $approved.tenantId, '--resource', 'https://storage.azure.com/') + $sub
    $r = Invoke-AzPowerShellCommand $tokenArguments 15
    Assert-True ($r.Succeeded -and $r.Data.accessToken -ceq 'PRIVATE_TOKEN_SENTINEL' -and
        [datetimeoffset]::Parse($r.Data.expiresOn) -gt [datetimeoffset]::UtcNow) 'Az secure token is captured privately in CLI-compatible in-memory shape with expiry'
    $r = Invoke-AzPowerShellCommand (@('account', 'get-access-token', '--tenant', $approved.tenantId,
        '--resource', 'https://graph.microsoft.com/') + $sub) 15
    Assert-True (-not $r.Succeeded) 'Ingestion token adapter rejects an unrelated audience'
    $env:VOICE_INFRA_FIXTURE_MODE = 'token-failure'
    $r = Invoke-AzPowerShellCommand $tokenArguments 15
    Assert-True (-not $r.Succeeded -and ($r | ConvertTo-Json -Depth 10) -notmatch 'PRIVATE_TOKEN_SENTINEL') 'Token acquisition failure cannot expose token/account exception content'
    $env:VOICE_INFRA_FIXTURE_MODE = ''
    $global:FixturePrivateTokenUsed = $false
    function global:Invoke-WebRequest {
        param($Method, $Uri, $Headers, $ContentType, $TimeoutSec, $MaximumRedirection, $UseBasicParsing, $ErrorAction, $Verbose, $Debug, $Body)
        $global:FixturePrivateTokenUsed = $Headers.Authorization -ceq 'Bearer PRIVATE_TOKEN_SENTINEL'
        throw (New-Object InvalidOperationException('PRIVATE_TOKEN_SENTINEL'))
    }
    try {
        $message = ''
        try {
            $null = & (Join-Path $scriptsRoot 'Import-Knowledge.ps1') -ManifestPath (Join-Path $PSScriptRoot 'fixtures\manifest.json') `
                -ContentRoot (Join-Path $PSScriptRoot 'fixtures') -AuthProvider AzPowerShell -Apply -ConfirmExclusiveMaintenance `
                -TenantId $approved.tenantId -ResourceGroup 'fixture-meeting-group' `
                -SearchName 'fixture-search' -OpenAIName 'fixture-ai' -StorageAccountName 'fixturestorage' -CommandTimeoutSeconds 15
        } catch { $message = $_.Exception.Message }
        Assert-True ($global:FixturePrivateTokenUsed -and $message -match 'Azure storage request failed' -and
            $message -notmatch 'PRIVATE_TOKEN_SENTINEL') 'Import AuthProvider selects Az metadata/token path and keeps transport failures redacted without uploads'
    } finally {
        Remove-Item Function:\Invoke-WebRequest
        Remove-Variable FixturePrivateTokenUsed -Scope Global
    }
} finally {
    $env:PSModulePath = $modulePath
    $env:VOICE_ASSISTANT_RELEASE_CONFIG = $previousReleaseConfig
    $env:VOICE_INFRA_FIXTURE_LOG = $oldLog
    $env:VOICE_INFRA_FIXTURE_MODE = $oldMode
    foreach ($file in @($log, $templateFile, $parameterFile)) { Remove-Item -LiteralPath $file -Force }
}
Write-Output "All $script:passed Azure process adapter checks passed. Authentication/resource effects were isolated fixtures only."
