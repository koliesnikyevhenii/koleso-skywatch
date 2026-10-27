#Requires -Version 5.1
<#
.SYNOPSIS
    Installs the git pre-commit hook that runs gitleaks on staged changes (spec C4).
.DESCRIPTION
    The hook is written with LF line endings so Git's sh can run it on Windows.
    Re-run after cloning; it overwrites any existing pre-commit hook.
#>
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$hooksDir = Join-Path $repoRoot '.git/hooks'
if (-not (Test-Path $hooksDir)) {
    throw "Not a git working tree: $repoRoot"
}

$hook = @(
    '#!/bin/sh'
    '# Installed by build/install-hooks.ps1. Blocks commits that contain secrets (spec C4).'
    'if ! command -v gitleaks >/dev/null 2>&1; then'
    '  echo "pre-commit: gitleaks is not installed or not on PATH (winget install gitleaks)." >&2'
    '  exit 1'
    'fi'
    'exec gitleaks git --staged --config .gitleaks.toml --redact --no-banner'
) -join "`n"

$hookPath = Join-Path $hooksDir 'pre-commit'
[System.IO.File]::WriteAllText($hookPath, $hook + "`n", (New-Object System.Text.UTF8Encoding $false))
Write-Host "Installed $hookPath"
