# Signing

AlliePack signs the built MSI with `signtool.exe` immediately after the WiX build completes. Add an optional top-level `signing:` block to your config. Configs that omit it are unaffected.

For file-level signing and the complete technical reference, see [docs/signing.md](../docs/signing.md).

---

## Choosing a provider

Exactly one signing provider is required. Choose based on how your certificate is stored:

| Provider | When to use |
|---|---|
| `thumbprint` | Certificate is installed in the Windows cert store on the build machine |
| `pfx` | Certificate is stored as a `.pfx` file |
| `azure` | Using Azure Trusted Signing (cloud-managed, no cert to store) |
| `command` | Any other signing tool |

---

## Certificate store (thumbprint)

The simplest option when a certificate is already installed on the build machine (or CI agent). No password is needed — the cert store protects the private key.

```yaml
signing:
  thumbprint: "ABCDEF1234567890ABCDEF1234567890ABCDEF12"
  timestampUrl: "http://timestamp.digicert.com"
```

To find the thumbprint of an installed certificate:

```powershell
Get-ChildItem Cert:\CurrentUser\My | Select-Object Thumbprint, Subject
Get-ChildItem Cert:\LocalMachine\My | Select-Object Thumbprint, Subject
```

---

## PFX file

Use this when your certificate is stored as a `.pfx` file, such as in a secure file store or a pipeline artifact.

```yaml
signing:
  pfx: "[YamlDir]/certs/MyApp.pfx"
  pfxPassword: "[SIGN_PASSWORD]"
  timestampUrl: "http://timestamp.digicert.com"
```

Pass the password at build time using `--define` — never hardcode it in the YAML:

```
AlliePack.exe allie-pack.yaml --define SIGN_PASSWORD=$(SIGN_PASSWORD) --output dist\MyApp.msi
```

In Azure DevOps, `$(SIGN_PASSWORD)` references a pipeline secret variable:

```yaml
- task: CmdLine@2
  displayName: Build and sign MSI
  inputs:
    script: |
      AlliePack.exe allie-pack.yaml
        --define SIGN_PASSWORD=$(SIGN_PASSWORD)
        --output $(Build.ArtifactStagingDirectory)\MyApp.msi
  env:
    SIGN_PASSWORD: $(SIGN_PASSWORD)
```

---

## Azure Trusted Signing

Azure Trusted Signing is a fully managed cloud signing service. Certificates are short-lived (valid for 3 days), so `timestampUrl` is **required**.

### Prerequisites

1. Create an Azure Trusted Signing resource in the Azure portal.
2. Assign yourself (or the pipeline identity) the **Certificate Profile Signer** role on the resource.
3. Install the client tools on the build machine:
   ```powershell
   winget install -e --id Microsoft.Azure.ArtifactSigningClientTools
   ```

### Configuration

```yaml
signing:
  azure:
    endpoint: "https://eus.codesigning.azure.net"   # match your Azure region
    account: "MySigningAccount"
    certificateProfile: "MyProfile"
    dlibPath: 'C:\Program Files\Microsoft\Azure Code Signing\x64\Azure.CodeSigning.Dlib.dll'
    correlationId: "[BUILD_ID]"      # optional; shows up in Azure signing logs
  timestampUrl: "http://timestamp.acs.microsoft.com"
```

AlliePack writes a `metadata.json` temp file at build time and passes it to `signtool`. You do not manage a separate `metadata.json`.

### Regional endpoints

| Region | Endpoint |
|---|---|
| East US | `https://eus.codesigning.azure.net` |
| West US | `https://wus.codesigning.azure.net` |
| West US 2 | `https://wus2.codesigning.azure.net` |
| West Europe | `https://weu.codesigning.azure.net` |
| North Europe | `https://neu.codesigning.azure.net` |

Use the endpoint in the same Azure region as your Trusted Signing resource.

### Azure DevOps pipeline

```yaml
- task: CmdLine@2
  displayName: Build and sign MSI (Azure Trusted Signing)
  inputs:
    script: |
      AlliePack.exe allie-pack.yaml
        --define BUILD_ID=$(Build.BuildId)
        --output $(Build.ArtifactStagingDirectory)\MyApp.msi
```

On Azure DevOps hosted agents, authentication uses the agent's managed identity. No credentials in the pipeline YAML needed.

---

## Custom signing command

Use `command:` when you need a tool that AlliePack doesn't natively support, such as `AzureSignTool.exe` or a corporate HSM-based signer. AlliePack substitutes `[TOKEN]` values and `{file}`, then runs the command via `cmd.exe`.

```yaml
signing:
  command: 'AzureSignTool.exe sign -kvu "[KV_URL]" -kvc "[CERT_NAME]" -fd sha256 -tr "http://timestamp.acs.microsoft.com" -td sha256 "{file}"'
```

```
AlliePack.exe allie-pack.yaml
  --define KV_URL=$(KEY_VAULT_URL)
  --define CERT_NAME=$(CERT_NAME)
  --output dist\MyApp.msi
```

`{file}` is replaced with the full path of each file being signed (and the MSI itself). Include your own quoting around `{file}` if the tool requires it.

---

## Signing packaged files

Add a `files:` subsection to any provider to sign `.exe`, `.dll`, and other binaries before WiX packages them into the MSI:

```yaml
signing:
  thumbprint: "ABCDEF1234567890ABCDEF1234567890ABCDEF12"
  timestampUrl: "http://timestamp.digicert.com"
  files:
    mode: unsigned                  # skip files that already carry a signature
    include: ["*.exe", "*.dll"]
    exclude: ["*.resources.dll"]    # satellite assemblies are typically skipped
```

### Modes

| Mode | Behavior |
|---|---|
| `unsigned` (default) | Skip files that already carry an Authenticode signature. Safe for third-party and Microsoft DLLs you redistribute. |
| `all` | Sign every candidate file, replacing any existing signature. |

### Include and exclude

`include` is a list of filename glob patterns (not paths). When omitted, AlliePack uses the Windows SIP registry to determine which files are signable — the same check `signtool` performs internally. Files without a registered SIP handler (text files, XML, images) are silently skipped regardless.

`exclude` is applied after `include` (or the SIP check). Use it to suppress specific files you don't want signed even if they would otherwise qualify.

### Diagnostic output

AlliePack prints a status line for every file during the signing step:

```
Signing packaged files...
  [signed  ] MyApp.exe
  [signed  ] MyLib.dll
  [skip    ] notepad.exe  -- already signed
  [skip    ] readme.txt   -- not signable (no SIP handler)
  [excluded] MyApp.resources.dll
  files: 2 signed, 1 already signed, 1 not signable
  signed : dist\MyApp.msi
```

Pass `--verbose` to also see files that were skipped for any reason.

---

## Local testing with a self-signed certificate

Use the included PowerShell script to create a test certificate:

```powershell
.\tools\New-TestSigningCert.ps1
```

This creates a `CN=AlliePack Test Signing` code signing certificate in your current user cert store and prints the thumbprint. Re-running the script returns the existing cert rather than creating a duplicate.

```yaml
signing:
  thumbprint: "<printed thumbprint>"
  # no timestampUrl needed for local testing
```

### Verifying the signature

```powershell
Get-AuthenticodeSignature ".\dist\MyApp.msi" | Select-Object Status, StatusMessage, SignerCertificate
```

Expected output with a self-signed cert:

```
Status    : NotTrusted
StatusMessage : A certificate chain processed, but terminated in a root
                certificate which is not trusted by the trust provider.
SignerCertificate : [Subject: CN=AlliePack Test Signing ...]
```

`NotTrusted` is correct for a self-signed certificate and confirms the MSI is signed and the signature is intact. The cert is simply not in a public trust store.

| Status | Meaning |
|---|---|
| `NotTrusted` | Signed; cert not in a trusted root store (expected for self-signed) |
| `Valid` | Signed with a publicly trusted certificate |
| `NotSigned` | Signing did not run or produced no output |
| `HashMismatch` | File was modified after signing — signature is broken |
| `UnknownError` | Something went wrong with the signature structure |

---

## Optional fields

| Field | Default | Notes |
|---|---|---|
| `timestampUrl` | none | RFC 3161 timestamp server. Required for Azure (3-day certs). Strongly recommended for production — keeps the signature valid after the cert expires. |
| `signToolPath` | auto-discovered | Explicit path to `signtool.exe`. Discovered via PATH and common Windows SDK locations when omitted. Not used by `command:`. |

## `signtool.exe` discovery order

1. `signing.signToolPath` in the config
2. First `signtool.exe` found on `PATH`
3. Newest version under `C:\Program Files (x86)\Windows Kits\10\bin\`
4. Newest version under `C:\Program Files\Windows Kits\10\bin\`

On Azure DevOps hosted agents, `signtool.exe` is already on `PATH` via the Windows SDK — no additional configuration needed.
