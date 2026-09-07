[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$failures = [System.Collections.Generic.List[string]]::new()

function Add-Failure {
    param([string]$Message)

    $failures.Add($Message)
}

$agentsPath = Join-Path $projectRoot 'AGENTS.md'
if (-not (Test-Path -LiteralPath $agentsPath)) {
    Add-Failure 'AGENTS.md is missing.'
}
else {
    $agentsLines = @(Get-Content -LiteralPath $agentsPath)
    if ($agentsLines.Count -lt 50 -or $agentsLines.Count -gt 150) {
        Add-Failure "AGENTS.md must stay between 50 and 150 lines; found $($agentsLines.Count)."
    }
}

$architecturePath = Join-Path $projectRoot 'docs\architecture.md'
if (-not (Test-Path -LiteralPath $architecturePath)) {
    Add-Failure 'docs/architecture.md is missing.'
}
else {
    $architecture = Get-Content -Raw -LiteralPath $architecturePath
    foreach ($heading in @('## Repository Map', '## Dependency Direction', '## Where to Look First')) {
        if (-not $architecture.Contains($heading)) {
            Add-Failure "docs/architecture.md is missing '$heading'."
        }
    }
}

$configPath = Join-Path $projectRoot '.codex\config.toml'
if (-not (Test-Path -LiteralPath $configPath)) {
    Add-Failure '.codex/config.toml is missing.'
}
else {
    $config = Get-Content -Raw -LiteralPath $configPath
    if ($config -notmatch '(?ms)\[plugins\."superpowers@openai-curated-remote"\].*?enabled\s*=\s*false') {
        Add-Failure 'The project-local config does not disable the Superpowers plugin.'
    }
}

$skills = @(
    @{ Name = 'systematic-debugging'; Trigger = 'root cause'; Exclusion = 'Do not use' },
    @{ Name = 'test-driven-development'; Trigger = 'observable behavior'; Exclusion = 'Do not use' },
    @{ Name = 'planning-architecture'; Trigger = 'architectural boundaries'; Exclusion = 'Do not use' },
    @{ Name = 'verification-before-completion'; Trigger = 'meaningful code changes'; Exclusion = 'Do not use' }
)

foreach ($skill in $skills) {
    $skillPath = Join-Path $projectRoot ".agents\skills\$($skill.Name)\SKILL.md"
    if (-not (Test-Path -LiteralPath $skillPath)) {
        Add-Failure "Skill '$($skill.Name)' is missing."
        continue
    }

    $content = Get-Content -Raw -LiteralPath $skillPath
    $frontmatter = [regex]::Match($content, '(?s)^---\r?\n(.*?)\r?\n---')
    if (-not $frontmatter.Success) {
        Add-Failure "Skill '$($skill.Name)' has invalid frontmatter."
        continue
    }

    if ($frontmatter.Groups[1].Value -notmatch "(?m)^name:\s*$([regex]::Escape($skill.Name))\s*$") {
        Add-Failure "Skill '$($skill.Name)' has the wrong frontmatter name."
    }

    $description = [regex]::Match($frontmatter.Groups[1].Value, '(?m)^description:\s*"?(.+?)"?\s*$').Groups[1].Value
    if (-not $description.StartsWith('Use when')) {
        Add-Failure "Skill '$($skill.Name)' description must start with 'Use when'."
    }
    if (-not $description.Contains($skill.Trigger) -or -not $description.Contains($skill.Exclusion)) {
        Add-Failure "Skill '$($skill.Name)' description lacks its trigger or exclusion."
    }

    $wordCount = @($content -split '\s+' | Where-Object { $_ }).Count
    if ($wordCount -gt 500) {
        Add-Failure "Skill '$($skill.Name)' exceeds 500 words; found $wordCount."
    }
}

$claudePath = Join-Path $projectRoot 'CLAUDE.md'
if (-not (Test-Path -LiteralPath $claudePath)) {
    Add-Failure 'CLAUDE.md is missing; run tools/Sync-AgentSkills.ps1 workflow setup.'
}
elseif ((Get-Content -Raw -LiteralPath $claudePath) -notmatch '(?m)^@AGENTS\.md\s*$') {
    Add-Failure 'CLAUDE.md must re-export AGENTS.md via a lone "@AGENTS.md" line and add no other guidance.'
}

$skillSync = Join-Path $projectRoot 'tools\Sync-AgentSkills.ps1'
if (-not (Test-Path -LiteralPath $skillSync)) {
    Add-Failure 'tools/Sync-AgentSkills.ps1 is missing.'
}
else {
    & pwsh -NoProfile -File $skillSync -Check *>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Add-Failure '.claude/skills is stale; run tools/Sync-AgentSkills.ps1.'
    }
}

$generatedMap = Join-Path $projectRoot 'docs\generated\assembly-map.md'
$mapGenerator = Join-Path $projectRoot 'tools\Update-RepositoryMap.ps1'
if (-not (Test-Path -LiteralPath $generatedMap)) {
    Add-Failure 'docs/generated/assembly-map.md is missing.'
}
if (-not (Test-Path -LiteralPath $mapGenerator)) {
    Add-Failure 'tools/Update-RepositoryMap.ps1 is missing.'
}
if ((Test-Path -LiteralPath $generatedMap) -and (Test-Path -LiteralPath $mapGenerator)) {
    $temporaryDirectory = Join-Path $projectRoot '.utmp'
    $temporaryMap = Join-Path $temporaryDirectory 'lean-workflow-assembly-map.md'
    [IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null

    try {
        & $mapGenerator -ProjectRoot $projectRoot -OutputPath $temporaryMap | Out-Null
        $expectedMap = Get-Content -Raw -LiteralPath $generatedMap
        $actualMap = Get-Content -Raw -LiteralPath $temporaryMap
        if ($expectedMap -cne $actualMap) {
            Add-Failure 'docs/generated/assembly-map.md is stale; run tools/Update-RepositoryMap.ps1.'
        }
    }
    catch {
        Add-Failure "Repository map validation failed: $($_.Exception.Message)"
    }
    finally {
        $resolvedTemporaryMap = [IO.Path]::GetFullPath($temporaryMap)
        $resolvedTemporaryDirectory = [IO.Path]::GetFullPath($temporaryDirectory) + [IO.Path]::DirectorySeparatorChar
        if ($resolvedTemporaryMap.StartsWith($resolvedTemporaryDirectory, [StringComparison]::OrdinalIgnoreCase)) {
            [IO.File]::Delete($resolvedTemporaryMap)
        }
    }
}

$scenarios = [ordered]@{
    'Typo/documentation edit' = 'none'
    'Trivial isolated fix' = 'none or verification'
    'Unknown bug' = 'systematic-debugging + verification-before-completion'
    'Behavioral feature' = 'test-driven-development + verification-before-completion'
    'Cross-boundary feature' = 'planning-architecture + test-driven-development when useful + verification-before-completion'
    'Large refactor' = 'planning-architecture + verification-before-completion; test-driven-development when behavior changes'
}

Write-Output 'Scenario policy:'
foreach ($scenario in $scenarios.GetEnumerator()) {
    Write-Output "- $($scenario.Key): $($scenario.Value)"
}

if ($failures.Count -gt 0) {
    Write-Error ($failures -join [Environment]::NewLine)
    exit 1
}

Write-Output 'Lean workflow validation passed.'
