# OnionHop Security Audit Report

Date: 2026-04-10  
Repository: `Silverdragon122/OnionHop`

## Findings (sorted by CVSS, highest first)

### 1) Privileged command injection in directory permission repair (Fixed)

- **Severity:** High
- **CVSS v3.1:** **8.6**
- **Vector:** `CVSS:3.1/AV:L/AC:L/PR:N/UI:R/S:C/C:H/I:H/A:H`
- **Affected code:** `OnionHop/src/OnionHopV2.Core/OnionHopClient.cs` (`FixBaseDirectoryPermissions`)

#### What was vulnerable
The macOS and Linux permission-repair paths built privileged shell commands by interpolating values into shell command strings (`osascript do shell script ...` and `pkexec sh -c ...`).  
This enabled shell metacharacter interpretation in privileged context.

#### Exploitability verification
I verified from code flow that:
1. The app executes these commands with elevated privileges (admin/root prompt path).
2. Command strings were composed with interpolated dynamic values and executed through shell parsing.
3. Parsed shell context + elevated execution means command injection can lead to root-level command execution when triggered.

#### Security impact
Successful exploitation can execute attacker-controlled commands as root/administrator, with full confidentiality/integrity/availability impact.

#### Fix implemented
Replaced shell-string execution with argument-safe privileged execution:
- **Linux:** removed `pkexec sh -c ...`; now runs `pkexec chown ...` and `pkexec chmod ...` using `ProcessStartInfo.ArgumentList`.
- **macOS:** replaced raw AppleScript shell-string composition with `MacAuthorization.RunScript(...)` and strict shell quoting via `MacAuthorization.QuoteShellArgument`.
- Added bounded timeout/error handling for privileged operations.

#### Validation after fix
- `dotnet build OnionHop/OnionHopV2.sln -c Release` ✅
- `dotnet test OnionHop/src/OnionHopV2.Tests/OnionHopV2.Tests.csproj -c Release` ⚠️ 1 pre-existing unrelated failing test:
  - `TorBridgeManagerTests.GetClientTransportPlugins_restores_webtunnel_client_from_bundle`

---

## Additional audit notes

- I reviewed high-risk surfaces: privileged command execution, local IPC surfaces, and dependency download/execution paths.
- No additional vulnerabilities were confirmed as directly exploitable with high confidence in this pass.
