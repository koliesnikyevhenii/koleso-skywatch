#Requires -Version 5.1
<#
.SYNOPSIS
    Skywatch gate chain: restore -> build -> test -> format -> gitleaks (staged + history).
.DESCRIPTION
    Runs every stage in order and stops at the first failure with a non-zero exit code.
    Works in Windows PowerShell 5.1 and PowerShell 7 (CI runs it with pwsh on ubuntu-latest).
#>
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

function Invoke-Stage {
    param([string]$Name, [scriptblock]$Command)

    Write-Host "==> $Name" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $Command
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $Name (exit code $LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
    Write-Host "OK: $Name" -ForegroundColor Green
}

if (-not (Get-Command gitleaks -ErrorAction SilentlyContinue)) {
    Write-Host 'FAILED: gitleaks is not installed or not on PATH (winget install gitleaks).' -ForegroundColor Red
    exit 1
}

Invoke-Stage 'restore' { dotnet restore }
Invoke-Stage 'build' { dotnet build -c Release --no-restore }
Invoke-Stage 'test' { dotnet test -c Release --no-build }
Invoke-Stage 'format' { dotnet format --verify-no-changes --no-restore }
Invoke-Stage 'gitleaks (staged)' { gitleaks git --staged --config .gitleaks.toml --redact --no-banner }
Invoke-Stage 'gitleaks (history)' { gitleaks git --config .gitleaks.toml --redact --no-banner }

Write-Host 'verify: all stages passed' -ForegroundColor Green
