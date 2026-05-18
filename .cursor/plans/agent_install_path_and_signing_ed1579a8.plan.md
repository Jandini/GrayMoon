---
name: Agent install path and signing
overview: Move install to `%ProgramFiles%\GrayMoon`, repoint existing service via `sc config`, extend uninstall to remove that install directory (with safe legacy ProgramData cleanup), and add GHA signing (Windows signtool or Linux osslsigncode/Key Vault) plus Docker COPY of the signed exe.
todos:
  - id: install-ps1-path
    content: Change install-agent.ps1 to use $env:ProgramFiles\GrayMoon; ensure directory creation and download target match
    status: pending
  - id: install-ps1-sc-config
    content: When GrayMoonAgent exists, after successful download run sc.exe config to set binPath to new exe + run -u hub URL; keep sc create path for new installs
    status: pending
  - id: dockerfile-artifact
    content: "Dockerfile: COPY prebuilt win-x64 graymoon-agent.exe from repo context; remove in-image win-x64 dotnet publish"
    status: pending
  - id: gha-sign-agent
    content: "docker-build.yml: job publishes win-x64 agent, signs (signtool on windows-latest or osslsigncode/AzureSignTool on ubuntu), uploads artifact; docker job needs + download + COPY into image"
    status: pending
  - id: uninstall-remove-install-dir
    content: "uninstall-agent.ps1: after service removal, delete Program Files\\GrayMoon install folder (graymoon-agent.exe and siblings); optional legacy ProgramData\\GrayMoon exe cleanup if present"
    status: pending
  - id: optional-readme-docker
    content: "Optional: README/local docker note for docker build without CI artifact"
    status: pending
isProject: false
---

# Agent install path, service migration, and code signing

## Current behavior

- [`src/GrayMoon.App/Resources/install-agent.ps1`](src/GrayMoon.App/Resources/install-agent.ps1) installs `graymoon-agent.exe` under `Join-Path $env:ProgramData 'GrayMoon'`, stops the service if running, downloads over the existing file, and **only** runs `sc create` when the service does not exist. If the service already exists, it never updates `binPath`, so a path change alone would leave the service pointing at the old executable.
- [`Dockerfile`](Dockerfile) publishes `GrayMoon.Agent` for `win-x64` inside the **Linux** SDK stage (unsigned PE). [`/.github/workflows/docker-build.yml`](.github/workflows/docker-build.yml) is **ubuntu-latest** only.
- Agent logs remain under `%ProgramData%\GrayMoon\logs` via [`RunCommandHandler.cs`](src/GrayMoon.Agent/Cli/RunCommandHandler.cs) (`CommonApplicationData`) - **no change recommended**; writable data under ProgramData is normal, and avoids permission issues under `Program Files`.

## 1. Install path: `Program Files\GrayMoon`

In [`install-agent.ps1`](src/GrayMoon.App/Resources/install-agent.ps1):

- Set install root to **`Join-Path $env:ProgramFiles 'GrayMoon'`** (equivalent to `Program Files\GrayMoon` on typical x64 systems where the published agent is `win-x64`).
- Keep administrator check; creating that directory and writing the exe already requires elevation.

## 1b. Uninstall: remove install directory under `Program Files`

Update [`uninstall-agent.ps1`](src/GrayMoon.App/Resources/uninstall-agent.ps1) so that **after** the service is removed (or if it was already absent):

- Define `$agentPath = Join-Path $env:ProgramFiles 'GrayMoon'` (same root as install).
- If that directory exists, remove it recursively (`Remove-Item -Recurse -Force`), or delete only known agent payloads (e.g. `graymoon-agent.exe` and any files the install script drops there) if you prefer a narrower delete - full folder removal is simplest if the directory is dedicated to GrayMoon.
- **Optional legacy cleanup:** If `%ProgramData%\GrayMoon\graymoon-agent.exe` still exists from older installs, delete that file (or the whole `%ProgramData%\GrayMoon` tree only if safe - today logs live under `%ProgramData%\GrayMoon\logs` from the running agent, so prefer removing **only** `graymoon-agent.exe` under ProgramData unless you confirm nothing else uses that folder). Safer default: remove `ProgramData\GrayMoon\graymoon-agent.exe` only when uninstalling, leave `logs` for user inspection or document manual cleanup.

## 2. Existing service: repoint `binPath` without removing old binaries

After the “stop service if running” block and **successful** download to the **new** `$agentExe` under Program Files:

- If **`Get-Service GrayMoonAgent`** exists, compute the same `binPath` string used at create time, e.g.  
  `"$agentExe" run -u "$hubUrl"`  
  using the script’s existing `{HUB_URL}` placeholder (already substituted by [`AgentEndpoints.cs`](src/GrayMoon.App/Api/Endpoints/AgentEndpoints.cs) when the script is served).
- Run **`sc.exe config $serviceName binPath= <value>`** with the same `binPath=` spacing rules as `sc create` (space after `binPath=`; outer quoting so the whole executable + arguments is one value). Check `$LASTEXITCODE` and surface errors clearly.
- Do **not** delete `%ProgramData%\GrayMoon` (or any old exe) - per your request.
- Then start the service (existing logic).

New installs (`-not $serviceExists`): unchanged flow except `$agentPath` / `$agentExe` point to Program Files.

**Caveat (document in script comment or release notes):** Re-running the install script from the app always sets `binPath` to `run -u "<current app hub URL>"`. That matches today’s “new install” behavior but would overwrite a manually edited service command line.

## 3. Pipeline: sign the Windows agent and use it in Docker

The Docker **Linux** stage cannot use Microsoft’s `signtool` without a Windows SDK image, but **Authenticode signing does not require Windows**: you can `dotnet publish -r win-x64` on **Ubuntu** (already works in the Dockerfile today) and sign the PE with **[osslsigncode](https://github.com/mtrojnar/osslsigncode)** or similar using a **PFX** and OpenSSL, or use **AzureSignTool** from a Linux job against **Azure Key Vault** (JWT auth, no exportable key). **Windows is not mandatory** - it is simply the most common choice because `signtool` ships on `windows-latest` and official docs target it.

**Two viable GHA patterns:**

| Approach | Runner | Typical tools |
|----------|--------|----------------|
| A (common) | `windows-latest` | `dotnet publish`, `signtool sign` (Windows SDK on image) |
| B | `ubuntu-latest` | `dotnet publish -r win-x64`, `osslsigncode sign` (install package or use prebuilt binary), or AzureSignTool + Key Vault |

Either way, produce a single **`graymoon-agent.exe`** artifact for the Docker job to `COPY` (same as the current plan: remove in-image win-x64 publish, consume artifact).

**Prerequisites for the signing workflow to work in GitHub Actions**

1. **Code-signing certificate**
   - **Public CA (recommended for distribution):** From DigiCert, Sectigo, SSL.com, etc., with the **Code Signing** EKU. EV vs standard OV is a business/reputation tradeoff. This is what helps **SmartScreen**, **Bitdefender**, and other products build **reputation** for your binary.
   - **Self-signed / private CA:** You *can* create a self-signed cert (e.g. `New-SelfSignedCertificate` with `Type CodeSigningCert` on Windows, or OpenSSL) and sign with `signtool` / `osslsigncode`. The PE will show as signed, and the pipeline is valid for **learning CI** or **strictly internal** machines where you deploy your own root as trusted. For **end users and AV**, a self-signed signature does **not** substitute for a public CA: the publisher is still “unknown” to the world, SmartScreen may still warn, and **false positives are unlikely to improve** in a meaningful way. Timestamping self-signed builds usually uses a public TSA anyway (fine) but does not fix trust of the publisher identity.
2. **Private key usable in CI**
   - **PFX path:** Export a `.pfx` (password-protected) from the CA workflow or certificate manager. Store in GitHub as a **secret** (e.g. Base64-encoded `WINDOWS_CODE_SIGNING_PFX_B64`) and a second secret for the **PFX password**. Restrict which branches/workflows can use these secrets (environment protection rules recommended).
   - **Key Vault path (preferred for production):** Certificate/key non-exportable in Azure Key Vault; GHA uses OIDC (`azure/login`) + AzureSignTool with `-kv` options - no long-lived PFX in secrets.
3. **Repository settings:** Secrets (and optional **Environments** with required reviewers) available to the workflow; note **fork PRs do not receive secrets** - workflow should `if: secrets... != ''` to skip signing and still upload an unsigned artifact, or fail only on protected branches as you prefer.
4. **Timestamping (strongly recommended):** A public **RFC3161** timestamp URL (your CA usually documents one, e.g. DigiCert/Sectigo HTTP TS) so the signature stays valid after the signing cert expires.
5. **For `signtool` on Windows runners:** `windows-latest` already includes the Windows SDK; locate `signtool.exe` under `Program Files (x86)\Windows Kits\10\bin\<version>\x64\` (or use `vswhere`/known version pin). No separate SDK install step is usually required.
6. **For `osslsigncode` on Linux:** Install the package or download a release binary in a workflow step; ensure OpenSSL can read the PFX (decrypt with password from secret).

**Secrets / variables (illustrative names)**

- PFX flow: `CODE_SIGNING_PFX_B64`, `CODE_SIGNING_PFX_PASSWORD`, optional `CODE_SIGNING_TIMESTAMP_URL`.
- Key Vault flow: Azure service principal or OIDC federated credential, `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, Key Vault URI, certificate name - per AzureSignTool docs.

**Docker build job** (`needs: build-agent-windows` or whatever job produces the exe):

- `actions/download-artifact` into a fixed path (e.g. `docker/agent-windows/graymoon-agent.exe`).
- [`Dockerfile`](Dockerfile): **remove** in-container `dotnet publish ... win-x64`; **`COPY`** the artifact into `/agent/publish-win/` as before.

**Local `docker build`:** Without the artifact, COPY fails - optional README or `docker-build.ps1` prerequisite (publish win-x64 to the expected folder).

## 4. Further actions to reduce AV false positives (recommendations)

- **Authenticode:** Prefer a **standard code-signing** cert (EV if budget allows); builds reputation faster. Use a **public timestamp** (`/tr` HTTP RFC3161) so signatures stay valid after cert expiry.
- **Microsoft:** Submit false positives via [Microsoft Security Intelligence](https://www.microsoft.com/en-us/wdsi/filesubmission) when SmartScreen/Defender flags the file.
- **Bitdefender:** Use their consumer/business false-positive report flow with the **signed** binary hash and version metadata.
- **Assembly / file metadata:** Ensure [`GrayMoon.Agent.csproj`](src/GrayMoon.Agent/GrayMoon.Agent.csproj) sets `Company`, `Product`, `Copyright`, and optionally `ApplicationIcon` / `AssemblyTitle` - improves heuristics vs generic “unknown .NET single-file”.
- **Behavior reputation:** Avoid obfuscation; keep download URLs **HTTPS** only in production; versioned, immutable release assets help.
- **Single-file:** `PublishSingleFile` can look suspicious to some heuristics; if problems persist after signing, consider publishing **non-single-file** for the downloadable Windows agent only (larger payload, often better reputation) - tradeoff to revisit only if needed.

No change required to [`AgentEndpoints.cs`](src/GrayMoon.App/Api/Endpoints/AgentEndpoints.cs) unless you add new placeholders (optional: `{INSTALL_DIR}` for support docs - not necessary for functionality).
