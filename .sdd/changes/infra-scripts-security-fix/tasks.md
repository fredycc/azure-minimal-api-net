# Tasks: infra-scripts-security-fix

> **Change**: Fix 13 security and best-practice issues across deploy.ps1, destroy-all.ps1, and infra/Program.cs
> **Project**: azure-minimal-api-net (.NET 10 Minimal API on Azure via Pulumi)

## Batch 1: deploy.ps1 security fixes

- [ ] **TASK-001**: Make ACR name dynamic — change `$AcrName = "acrdoctorsapidev"` to `$AcrName = "acrdoctorsapi$Env"` at line 103 so it matches the naming convention `acrdoctorsapi{env}` used in Program.cs:168 — `deploy.ps1:103`
- [ ] **TASK-002**: Replace plaintext `$env:PULUMI_CONFIG_PASSPHRASE` with secure temp-file approach — write passphrase to a temp file, set `$env:PULUMI_CONFIG_PASSPHRASE_FILE`, run pulumi commands, then delete temp file in a `try/finally` block to guarantee cleanup even on failure — `deploy.ps1:140-147`
- [ ] **TASK-003**: Fix docker build — remove `| Out-Null` from `docker build` at line 192, capture output, and check `$LASTEXITCODE` to fail fast on build errors — `deploy.ps1:192`
- [ ] **TASK-004**: Fix docker push — remove `| Out-Null` from `docker push` at line 201, capture output, and check `$LASTEXITCODE` to fail fast on push errors — `deploy.ps1:201`

## Batch 2: infra/Program.cs critical fixes

- [ ] **TASK-005**: Remove duplicate KV secret creation — delete the `AzureNative.KeyVault.Secret` resources for `sqlConnectionStringSecret` (lines 325-334) and `jwtSigningKeySecret` (lines 343-352). The Container App secrets at lines 380-392 already hold the values inline via Pulumi state; storing them again in KV adds no value for dev and creates confusion about the source of truth. Keep the CA secrets (app reads from env vars via SecretRef). Remove `DependsOn` references to these deleted secrets from the ContainerApp CustomResourceOptions (line 431). — `Program.cs:325-352, 431`
- [ ] **TASK-006**: Replace `Pulumi.Command.Local.Command` firewall rule with native `AzureNative.Sql.FirewallRule` — use `containerApp.OutboundIpAddresses.Apply()` to extract the first outbound IP and create a native firewall rule. **Fallback**: If `OutboundIpAddresses` is NOT available as a Pulumi Output on the ContainerApp type, keep the LocalCommand but make it cross-platform by replacing `powershell -Command` with a bash-compatible script that works on both Windows and Linux. — `Program.cs:487-492`

## Batch 3: infra/Program.cs warnings + suggestions

- [ ] **TASK-007**: Make ASPNETCORE_ENVIRONMENT environment-aware — change line 410 from `Value = "Development"` to `Value = env == "prod" ? "Production" : "Development"` so production deployments get the correct environment — `Program.cs:410`
- [ ] **TASK-008**: Add PrincipalType explanatory comments — add `// "ServicePrincipal" is required for SystemAssigned Managed Identity in Azure RBAC` comment before both RoleAssignment resources at lines 443 and 459 — `Program.cs:443, 459`
- [ ] **TASK-009**: Fix section numbering — renumber sections to be sequential 1-14. Current issue: section "9" is duplicated (9b JWT key sits between sections 8 and 9). Fix: 8=KV, 9=Container App, 10=AcrPull RBAC, 11=KV Secrets User RBAC, 12=KV Admin RBAC, 13=Firewall Rule (CA), 14=Diagnostic Settings. Also update the architecture diagram at lines 8-25 to match. — `Program.cs:8-25, 249, 354, 433, 448, 463, 479, 494`
- [ ] **TASK-010**: Extract magic number for GB — add `const long GB = 1_073_741_824L;` before the cost mode switch (line 99) and replace all `1073741824L` and `2147483648L` and `5368709120L` with `1 * GB`, `2 * GB`, `5 * GB` respectively. Also update the console output at line 113 to use `sqlMaxSize / GB` — `Program.cs:99-106, 113`

## Batch 4: destroy-all.ps1 fixes

- [ ] **TASK-011**: Add secure Pulumi passphrase handling — add the same temp-file passphrase block from TASK-002 to destroy-all.ps1 before the `pulumi destroy` command at line 35, with try/finally cleanup — `destroy-all.ps1:34-35`
- [ ] **TASK-012**: Add warning about other potential soft-deleted resources — add a comment/message before the Key Vault purge section noting that other resources (SQL servers, Container App Environments) may also have soft-delete behavior and could need manual cleanup — `destroy-all.ps1:37-38`
- [ ] **TASK-013**: Add `--yes` flag to `az keyvault purge` at line 43 to avoid interactive confirmation prompts in automated runs — `destroy-all.ps1:43`

## Batch 5: Verification

- [ ] **TASK-014**: Verify deploy.ps1 — run `pwsh -NoProfile -Command "& { $errors = @(); $null = [System.Management.Automation.PSParser]::Tokenize((Get-Content 'deploy.ps1' -Raw), [ref]$errors); if ($errors) { $errors | ForEach-Object { Write-Error $_ } } else { Write-Host 'Syntax OK' } }"` to confirm no PowerShell syntax errors
- [ ] **TASK-015**: Verify destroy-all.ps1 — same syntax check as TASK-014 for destroy-all.ps1
- [ ] **TASK-016**: Verify Program.cs compiles — run `dotnet build infra/` to confirm the C# code compiles without errors after all changes
- [ ] **TASK-017**: Cross-check consistency — verify `$AcrName` in deploy.ps1 matches the pattern `acrdoctorsapi{env}` used in Program.cs, verify section numbers are sequential 1-14, verify no orphaned references to deleted secrets
