---
name: verify
description: Run the Skywatch gate chain (restore, Release build with warnings as errors, tests, dotnet format, gitleaks over staged changes and history). Must be green before every commit.
---

# verify

Run from the repository root:

```powershell
powershell -NoProfile -File build/verify.ps1   # Windows PowerShell 5.1
pwsh build/verify.ps1                          # PowerShell 7 (same script, used by CI)
```

Stages run in order and the script stops at the first failure with exit code 1:

1. `dotnet restore`
2. `dotnet build -c Release --no-restore` (warnings are errors)
3. `dotnet test -c Release --no-build` (Microsoft.Testing.Platform; zero tests is a failure)
4. `dotnet format --verify-no-changes --no-restore`
5. `gitleaks git --staged` with `.gitleaks.toml`
6. `gitleaks git` (full history) with `.gitleaks.toml`

Report each stage's result with the actual output of the failing stage.

If either gitleaks stage reports a finding: stop immediately. Do not commit, do not "fix forward", do not edit `.gitleaks.toml` to silence it. Report the finding (rule id and file, never the secret value) to the user.

Prerequisite: `gitleaks` on PATH (`winget install gitleaks`). The pre-commit hook is installed with `build/install-hooks.ps1`.
