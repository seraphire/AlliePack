# Code Signing

AlliePack can sign the built MSI with `signtool.exe` immediately after the WiX
build completes.  Add an optional top-level `signing:` block to your config.
Configs that omit it are unaffected.

## Configuration

Exactly one of `thumbprint` or `pfx` must be supplied.

### File signing (optional)

Add a `files:` subsection to sign individual binaries **before** they are
packaged into the MSI.  When absent, only the MSI itself is signed.

```yaml
signing:
  thumbprint: "ABCDEF1234567890ABCDEF1234567890ABCDEF12"
  timestampUrl: "http://timestamp.digicert.com"
  files:
    mode: unsigned                  # default: skip already-signed files
    include: ["*.exe", "*.dll"]     # omit to let Windows decide (see below)
    exclude: ["*.resources.dll"]    # always applied on top of include
```

**`mode`**

| Value | Behaviour |
|---|---|
| `unsigned` (default) | Skip files that already carry an Authenticode signature.  Safe for third-party and Microsoft DLLs you redistribute. |
| `all` | Sign every candidate file, replacing any existing signature. |

**`include`**

Filename glob patterns (not full paths).  When omitted, AlliePack calls
`CryptSIPRetrieveSubjectGuid` for each file — the same Windows API call
signtool makes internally.  Files with no registered SIP handler (text files,
images, YAML, etc.) are silently skipped.  If the SIP table on the build
machine can't sign a file, signtool couldn't either, so no information is lost
by delegating to it.

**`exclude`**

Filename globs applied after `include` (or the SIP check).  Useful for
suppressing specific files you do not want signed even if they would otherwise
qualify.

### Signing order

```
sign files in-place  ->  BuildMsi()  ->  sign MSI
```

Files are signed in their original build output locations before WiX reads
them.  This is the standard practice across the Windows packaging ecosystem
(electron-builder, Advanced Installer, Azure DevOps signing tasks).

### Diagnostic output

AlliePack prints a status line for every file it signs or skips during the
file signing step.  Pass `--verbose` to see skipped files as well.

```
Signing packaged files...
  [signed  ] MyApp.exe
  [signed  ] MyLib.dll
  [skip    ] notepad.exe -- already signed
  [skip    ] readme.txt  -- not signable (no SIP handler)
  [excluded] MyApp.resources.dll
  files: 2 signed, 1 already signed, 1 not signable
  signed : dist\MyApp.msi
```

`[failed]` lines are always shown regardless of verbosity.  A signing failure
aborts the build after all files have been attempted so you see the full list
of failures in one pass.

### Certificate store (thumbprint)

```yaml
signing:
  thumbprint: "ABCDEF1234567890ABCDEF1234567890ABCDEF12"
  timestampUrl: "http://timestamp.digicert.com"
```

`signtool.exe` searches the current user and machine cert stores for the
certificate matching that SHA1 thumbprint.  No password is needed because the
private key is already protected by the store.

### PFX file

```yaml
signing:
  pfx: "certs/MyApp.pfx"              # path resolved via aliases and tokens
  pfxPassword: "[SIGN_PASSWORD]"      # injected at build time via --define
  timestampUrl: "http://timestamp.digicert.com"
```

Inject the password from a pipeline secret variable rather than storing it in
the YAML:

```
AlliePack.exe allie-pack.yaml --define SIGN_PASSWORD=$(SIGN_PASSWORD) --output dist\MyApp.msi
```

### Optional fields

| Field | Default | Notes |
|---|---|---|
| `timestampUrl` | none | RFC 3161 timestamp server.  Strongly recommended for production so the signature stays valid after the cert expires. |
| `signToolPath` | auto-discovered | Explicit path to `signtool.exe`.  Discovered via PATH then common Windows SDK locations when omitted. |

## Tool discovery

`signtool.exe` is located in this order:

1. `signing.signToolPath` in the config (resolved via aliases/tokens)
2. First `signtool.exe` found on `PATH`
3. Newest version under `C:\Program Files (x86)\Windows Kits\10\bin\`
4. Newest version under `C:\Program Files\Windows Kits\10\bin\`

On Azure DevOps hosted agents the tool is already on `PATH` via the Windows SDK,
so no additional configuration is needed.

## Azure Pipelines example

```yaml
- task: CmdLine@2
  displayName: Build and sign MSI
  inputs:
    script: >
      AlliePack.exe allie-pack.yaml
      --define SIGN_PASSWORD=$(SIGN_PASSWORD)
      --output $(Build.ArtifactStagingDirectory)\MyApp.msi
  env:
    SIGN_PASSWORD: $(SIGN_PASSWORD)   # secret pipeline variable
```

For the thumbprint approach, install the certificate first using the
**InstallCertificate** task or a PowerShell step, then reference its thumbprint
directly in the YAML (or inject it via `--define`).

---

## Local testing with a self-signed certificate

Production certificates should never be stored in source control.  For local
testing, create a self-signed certificate in the Windows cert store:

```powershell
.\tools\New-TestSigningCert.ps1
```

The script creates a "CN=AlliePack Test Signing" code signing certificate valid
for two years and prints the thumbprint.  Re-running the script returns the same
certificate instead of creating a duplicate.

Paste the thumbprint into your config:

```yaml
signing:
  thumbprint: "4DE2EBF5DBEAF2AD263A90B90F176F66C114C6F6"
  # no timestampUrl needed for local testing
```

### Verifying the signature

After a successful build, confirm the MSI is signed with PowerShell:

```powershell
Get-AuthenticodeSignature ".\MyApp.msi" | Select-Object Status, StatusMessage, SignerCertificate
```

Expected output when using a self-signed certificate:

```
Status         : NotTrusted
StatusMessage  : A certificate chain processed, but terminated in a root
                 certificate which is not trusted by the trust provider.
SignerCertificate : [Subject: CN=AlliePack Test Signing ...]
```

`NotTrusted` is the correct result for a self-signed certificate and confirms
that:

- The MSI **has** a digital signature (it is not `NotSigned`)
- The signature hash is intact (file was not modified after signing)
- The certificate chain resolved correctly
- The cert is simply not in a public trust store -- as expected for a local test cert

The values you do **not** want to see:

| Status | Meaning |
|---|---|
| `NotSigned` | Signing did not run or produced no output |
| `HashMismatch` | File was modified after signing -- signature is broken |
| `UnknownError` | Something went wrong with the signature structure |

`Valid` means the cert is trusted by the machine -- this requires adding the
self-signed cert to your local Trusted Root store, which is not recommended as
it widens your machine's trust surface.  `NotTrusted` is sufficient for testing
the signing pipeline.
