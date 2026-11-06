#Requires -Version 7.4
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidatePattern('\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z')][string]$Repository,
    [string]$GitHubCli = 'gh',
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$rootPath = Split-Path $PSScriptRoot -Parent
$settings = Get-Content -LiteralPath (Join-Path $rootPath '.github/repository-settings.json') -Raw | ConvertFrom-Json
$labels = @(Get-Content -LiteralPath (Join-Path $rootPath '.github/labels.json') -Raw | ConvertFrom-Json)
$endpoint = "repos/$Repository"

function Invoke-GitHub([string]$Method, [string]$Path, $Body = $null) {
    $arguments = @('api', '--method', $Method, '-H', 'Accept: application/vnd.github+json', '-H', 'X-GitHub-Api-Version: 2026-03-10', $Path)
    $result = if ($null -eq $Body) { & $GitHubCli @arguments }
        else { $Body | ConvertTo-Json -Depth 20 -Compress | & $GitHubCli @arguments --input - }
    if ($LASTEXITCODE -ne 0) { throw "GitHub request failed: $Method $Path" }
    if ($result) { return ($result -join "`n" | ConvertFrom-Json) }
}
function Set-ManagedRuleset($Definition) {
    $existing = @(Invoke-GitHub GET "$endpoint/rulesets?per_page=100" | Where-Object name -CEQ $Definition.name)
    if ($existing.Count -gt 1) { throw "Duplicate managed ruleset: $($Definition.name)" }
    if ($existing.Count -eq 1) { $null = Invoke-GitHub PUT "$endpoint/rulesets/$($existing[0].id)" $Definition }
    else { $null = Invoke-GitHub POST "$endpoint/rulesets" $Definition }
}

# Plan is local and read-only. Applying requires an already created public repository,
# an authenticated administrator, and a successful bootstrap CI run.
if (!$Apply) {
    [pscustomobject]@{ Repository = $Repository; Mode = 'Plan'; Settings = $settings; Labels = $labels; SecretsUploaded = $false }
    return
}
$repo = Invoke-GitHub GET $endpoint
if ($repo.private -or !$repo.permissions.admin) { throw 'A public repository with administrator access is required.' }
$account = Invoke-GitHub GET user
$actionsApp = Invoke-GitHub GET apps/github-actions
if (!$PSCmdlet.ShouldProcess($Repository, 'Configure public repository settings, security, release environment, labels and rulesets')) { return }

$null = Invoke-GitHub PATCH $endpoint $settings.repository
$null = Invoke-GitHub PUT "$endpoint/topics" @{ names = $settings.topics }
$null = Invoke-GitHub PUT "$endpoint/actions/permissions/workflow" $settings.actions
$null = Invoke-GitHub PUT "$endpoint/actions/permissions" @{ enabled = $true; allowed_actions = 'selected'; sha_pinning_required = $true }
$null = Invoke-GitHub PUT "$endpoint/actions/permissions/selected-actions" @{ github_owned_allowed = $true; verified_allowed = $false; patterns_allowed = @() }

$existingLabels = @(Invoke-GitHub GET "$endpoint/labels?per_page=100")
foreach ($label in $labels) {
    if ($existingLabels.name -contains $label.name) {
        $null = Invoke-GitHub PATCH "$endpoint/labels/$([Uri]::EscapeDataString($label.name))" @{ new_name = $label.name; color = $label.color; description = $label.description }
    } else { $null = Invoke-GitHub POST "$endpoint/labels" $label }
}

$null = Invoke-GitHub PUT "$endpoint/vulnerability-alerts"
$null = Invoke-GitHub PUT "$endpoint/automated-security-fixes"
$null = Invoke-GitHub PUT "$endpoint/private-vulnerability-reporting"
$null = Invoke-GitHub PATCH $endpoint @{ security_and_analysis = @{ secret_scanning = @{ status = 'enabled' }; secret_scanning_push_protection = @{ status = 'enabled' } } }

Set-ManagedRuleset @{
    name = 'main: review and verification'; target = 'branch'; enforcement = 'active'; bypass_actors = @()
    conditions = @{ ref_name = @{ include = @('~DEFAULT_BRANCH'); exclude = @() } }
    rules = @(
        @{ type = 'deletion' }, @{ type = 'non_fast_forward' }, @{ type = 'required_linear_history' },
        @{ type = 'pull_request'; parameters = @{ required_approving_review_count = 0; dismiss_stale_reviews_on_push = $true; require_code_owner_review = $false; require_last_push_approval = $false; required_review_thread_resolution = $true } },
        @{ type = 'required_status_checks'; parameters = @{ strict_required_status_checks_policy = $true; required_status_checks = @($settings.requiredChecks | ForEach-Object { @{ context = $_; integration_id = $actionsApp.id } }) } }
    )
}
Set-ManagedRuleset @{
    name = 'releases: immutable version tags'; target = 'tag'; enforcement = 'active'; bypass_actors = @()
    conditions = @{ ref_name = @{ include = @('refs/tags/v*'); exclude = @() } }
    rules = @(@{ type = 'deletion' }, @{ type = 'non_fast_forward' })
}
$null = Invoke-GitHub PUT "$endpoint/environments/release" @{
    wait_timer = 0; prevent_self_review = $false
    reviewers = @(@{ type = 'User'; id = $account.id })
    deployment_branch_policy = @{ protected_branches = $false; custom_branch_policies = $true }
}
$policies = Invoke-GitHub GET "$endpoint/environments/release/deployment-branch-policies?per_page=100"
$unexpected = @($policies.branch_policies | Where-Object { $_.name -cne $repo.default_branch -or $_.type -cne 'branch' })
if ($unexpected.Count) { throw 'The release environment has additional branch/tag policies. Review them manually; this script does not remove unrelated policies.' }
if (!@($policies.branch_policies).Count) {
    $null = Invoke-GitHub POST "$endpoint/environments/release/deployment-branch-policies" @{ name = $repo.default_branch; type = 'branch' }
}
[pscustomobject]@{
    Repository = $Repository; Mode = 'Applied'; DefaultBranch = $repo.default_branch
    LabelsManaged = $labels.Count; RequiredChecks = $settings.requiredChecks
    PrivateVulnerabilityReporting = $true; SecretScanning = $true; PushProtection = $true
    ReleaseReviewer = $account.login; SecretsUploaded = $false
}
