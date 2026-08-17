Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$contractRoot = Join-Path $repositoryRoot 'docs/contracts/v1'
$schemaRoot = Join-Path $contractRoot 'schemas'

function Read-JsonObject([string]$Path) {
    Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

$jsonFiles = @(Get-ChildItem -LiteralPath $contractRoot -Recurse -Filter '*.json')
Assert-True ($jsonFiles.Count -gt 0) 'No Stage 0 JSON contracts were found.'
foreach ($file in $jsonFiles) {
    $null = Read-JsonObject $file.FullName
}

$requiredSchemas = @(
    'project-manifest',
    'forge-document',
    'generated-artifact',
    'event',
    'handoff',
    'finding',
    'node-result',
    'project-snapshot',
    'suggested-action',
    'user-config',
    'language-pack',
    'provider-health',
    'startup-check',
    'diagnostic-bundle',
    'execution-profile'
)
$schemas = @{}
foreach ($name in $requiredSchemas) {
    $path = Join-Path $schemaRoot "$name.schema.json"
    Assert-True (Test-Path -LiteralPath $path) "Missing schema: $name"
    $schema = Read-JsonObject $path
    Assert-True ($schema.'$schema' -eq 'https://json-schema.org/draft/2020-12/schema') "$name does not use JSON Schema Draft 2020-12."
    Assert-True ($schema.'$id' -eq "https://forge.dev/schemas/v1/$name.schema.json") "$name has an invalid versioned schema ID."
    Assert-True ($schema.type -eq 'object') "$name must define an object boundary."
    Assert-True ($schema.additionalProperties -eq $false) "$name must reject unknown root properties."
    Assert-True ($schema.required -contains 'schema_version') "$name must require schema_version."
    $schemas[$name] = $schema
}
Assert-True (($schemas.Values | ForEach-Object { $_.'$id' } | Sort-Object -Unique).Count -eq $requiredSchemas.Count) 'Schema IDs must be unique.'

$stateRegistry = Read-JsonObject (Join-Path $contractRoot 'state-machines.json')
Assert-True ($stateRegistry.contract_version -eq '1.1.0') 'State-machine contract version must be 1.1.0.'
foreach ($machineEntry in $stateRegistry.machines.PSObject.Properties) {
    $name = $machineEntry.Name
    $machine = $machineEntry.Value
    $states = @($machine.transitions.PSObject.Properties.Name)
    Assert-True ($states -contains $machine.initial) "$name initial state is undefined."
    foreach ($terminal in $machine.terminal) {
        Assert-True ($states -contains $terminal) "$name terminal state '$terminal' is undefined."
        $terminalTargets = @($machine.transitions.PSObject.Properties[$terminal].Value)
        Assert-True ($terminalTargets.Count -eq 0) "$name terminal state '$terminal' has an outgoing transition."
    }
    foreach ($source in $states) {
        $targets = @($machine.transitions.PSObject.Properties[$source].Value)
        Assert-True (@($targets | Sort-Object -Unique).Count -eq $targets.Count) "$name state '$source' has duplicate transitions."
        foreach ($target in $targets) {
            Assert-True ($states -contains $target) "$name transition '$source -> $target' targets an undefined state."
        }
    }
}

$capabilityRegistry = Read-JsonObject (Join-Path $contractRoot 'capabilities.json')
$capabilityIds = @($capabilityRegistry.capabilities | ForEach-Object { $_.id })
Assert-True (($capabilityIds | Sort-Object -Unique).Count -eq $capabilityIds.Count) 'Capability IDs must be unique.'
foreach ($capability in $capabilityRegistry.capabilities) {
    foreach ($field in @('contract', 'cli', 'desktop', 'permission', 'acceptance')) {
        Assert-True (-not [string]::IsNullOrWhiteSpace($capability.$field)) "Capability '$($capability.id)' is missing $field."
    }
    Assert-True ($capability.events.Count -gt 0) "Capability '$($capability.id)' has no typed event."
}

$recommendationRegistry = Read-JsonObject (Join-Path $contractRoot 'recommendations.json')
$priorities = @($recommendationRegistry.ranking.attention_order)
foreach ($action in $recommendationRegistry.actions) {
    Assert-True ($priorities -contains $action.priority) "Action '$($action.id)' has an unknown priority."
    foreach ($field in @('rationale_key', 'preconditions', 'safety_class', 'target', 'command', 'stale_behavior')) {
        Assert-True ($null -ne $action.$field) "Action '$($action.id)' is missing $field."
    }
    if ($action.safety_class -ne 'read') {
        Assert-True ($action.stale_behavior -eq 'reject_without_side_effect') "Mutating action '$($action.id)' must reject stale state without side effects."
    }
}
foreach ($invariant in @('validate_command', 'authorize', 'confirm_by_safety_class', 'check_expected_state_version', 'enforce_idempotency')) {
    Assert-True ($recommendationRegistry.dispatch_invariants -contains $invariant) "Missing recommendation dispatch invariant: $invariant"
}

$configuration = Read-JsonObject (Join-Path $contractRoot 'configuration.json')
$configurationKeys = @($configuration.keys | ForEach-Object { $_.key })
Assert-True (($configurationKeys | Sort-Object -Unique).Count -eq $configurationKeys.Count) 'Configuration keys must have one owner.'
foreach ($key in $configuration.keys) {
    Assert-True (@('user', 'project') -contains $key.scope) "Configuration key '$($key.key)' has an invalid scope."
    $hasDynamicDefault = [bool]$key.PSObject.Properties['default_is_dynamic']
    Assert-True ($null -ne $key.default -or $null -ne $key.inherits -or $hasDynamicDefault) "Configuration key '$($key.key)' needs a default, an inheritance source, or an explicit default_is_dynamic note."
    if ($key.session_override) {
        Assert-True ($key.scope -eq 'user') "Project key '$($key.key)' cannot have a session override."
    }
}
Assert-True ($configuration.wrong_scope_code -eq 'configuration_scope_violation') 'Wrong-scope diagnostics must be invariant.'

$validatorProject = Join-Path $repositoryRoot 'tests/Forge.Tests/Forge.Tests.csproj'
& dotnet restore $validatorProject --locked-mode -p:EnableWindowsTargeting=true
if ($LASTEXITCODE -ne 0) {
    throw 'Contract validator restore failed.'
}

& dotnet test $validatorProject --no-restore --framework net10.0 --filter 'Category=Contracts'
if ($LASTEXITCODE -ne 0) {
    throw 'Draft 2020-12 schema validation failed.'
}

$machineCount = @($stateRegistry.machines.PSObject.Properties).Count
Write-Host "Stage 0 contract gate passed: $($jsonFiles.Count) JSON files, $($requiredSchemas.Count) schemas, $machineCount state machines, $($capabilityRegistry.capabilities.Count) capabilities."
