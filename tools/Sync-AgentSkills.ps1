#requires -Version 7.0
<#
.SYNOPSIS
  Regenerates .claude/skills/ from the canonical skills in .agents/skills/.

.DESCRIPTION
  The repository keeps one canonical copy of each workflow skill under
  .agents/skills/<name>/. OpenAI Codex reads those directly (plus the
  per-skill agents/openai.yaml metadata). Claude Code instead discovers
  skills under .claude/skills/<name>/SKILL.md.

  This script mirrors each canonical SKILL.md into .claude/skills/, normalizing
  the YAML frontmatter fence (some canonical files use a decorative dashed
  closing line that strict parsers reject). The generated files are marked
  read-only-by-convention with a header comment; edit the .agents/ copy and
  re-run this script.

  Run after adding or editing anything under .agents/skills/:
      pwsh -File tools/Sync-AgentSkills.ps1
  Use -Check in CI to fail when the generated tree is stale.
#>
[CmdletBinding()]
param(
    [switch]$Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repoRoot '.agents/skills'
$targetRoot = Join-Path $repoRoot '.claude/skills'

if (-not (Test-Path $sourceRoot)) {
    throw "Source skills directory not found: $sourceRoot"
}

function Get-SkillContent {
    param([string]$Path)

    $text = Get-Content -LiteralPath $Path -Raw
    $text = $text -replace "`r`n", "`n"

    # Split leading frontmatter block: opening '---' line, body of key: value
    # lines, then a closing line that is '---' or a run of dashes.
    if ($text -notmatch '(?s)^\s*-{3,}[ \t]*\n(?<fm>.*?)\n[ \t]*-{3,}[ \t]*\n(?<body>.*)$') {
        throw "Could not parse frontmatter in $Path"
    }

    $frontmatter = $Matches['fm'].Trim("`n")
    $body = $Matches['body']

    $name = $null
    $description = $null
    foreach ($line in ($frontmatter -split "`n")) {
        if ($line -match '^\s*name:\s*(.+?)\s*$') { $name = $Matches[1].Trim('"', "'") }
        elseif ($line -match '^\s*description:\s*(.+?)\s*$') { $description = $Matches[1].Trim() }
    }
    if (-not $name) { throw "Missing 'name' in frontmatter of $Path" }
    if (-not $description) { throw "Missing 'description' in frontmatter of $Path" }

    # Quote the description if it is not already quoted.
    if ($description -notmatch '^".*"$' -and $description -notmatch "^'.*'$") {
        $description = '"' + ($description -replace '"', '\"') + '"'
    }

    $header = @(
        '---'
        "name: $name"
        "description: $description"
        '---'
        ''
        '<!-- Generated from .agents/skills/' + $name + '/SKILL.md by tools/Sync-AgentSkills.ps1. Do not edit here. -->'
        ''
        ''
    ) -join "`n"

    return ($header + $body.TrimStart("`n")).TrimEnd("`n") + "`n"
}

$skillDirs = Get-ChildItem -LiteralPath $sourceRoot -Directory | Sort-Object Name
$stale = @()

foreach ($dir in $skillDirs) {
    $sourceSkill = Join-Path $dir.FullName 'SKILL.md'
    if (-not (Test-Path $sourceSkill)) {
        Write-Warning "Skipping $($dir.Name): no SKILL.md"
        continue
    }

    $rendered = Get-SkillContent -Path $sourceSkill
    $targetDir = Join-Path $targetRoot $dir.Name
    $targetSkill = Join-Path $targetDir 'SKILL.md'

    $current = if (Test-Path $targetSkill) { (Get-Content -LiteralPath $targetSkill -Raw) -replace "`r`n", "`n" } else { $null }

    if ($current -ne $rendered) {
        $stale += $dir.Name
        if (-not $Check) {
            New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
            # Write LF-normalized to match the canonical source files.
            [System.IO.File]::WriteAllText($targetSkill, $rendered)
            Write-Host "Updated .claude/skills/$($dir.Name)/SKILL.md"
        }
    }
}

# Remove generated skills whose canonical source is gone.
if (Test-Path $targetRoot) {
    $validNames = $skillDirs.Name
    foreach ($dir in (Get-ChildItem -LiteralPath $targetRoot -Directory)) {
        if ($dir.Name -notin $validNames) {
            $stale += $dir.Name
            if (-not $Check) {
                Remove-Item -LiteralPath $dir.FullName -Recurse -Force
                Write-Host "Removed stale .claude/skills/$($dir.Name)"
            }
        }
    }
}

if ($Check -and $stale.Count -gt 0) {
    Write-Error "Generated .claude/skills is stale for: $($stale -join ', '). Run tools/Sync-AgentSkills.ps1."
    exit 1
}

if ($stale.Count -eq 0) {
    Write-Host "Claude skills already in sync."
}
