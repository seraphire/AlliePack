# Signing

AlliePack signs the built MSI with `signtool.exe` immediately after the WiX build completes. Add an optional top-level `signing:` block to your config. A config with no `signing:` block and no `sign:` on any entry signs nothing.

For file-level signing and the complete technical reference, see [docs/signing.md](../docs/signing.md).

---

## Choosing a provider

Name at most one signing provider. With none, AlliePack picks the certificate itself (see [Choosing the certificate automatically](#choosing-the-certificate-automatically)). Choose based on how your certificate is stored:

| Provider | When to use |
|---|---|
| `thumbprint` | Certificate is installed in the Windows cert store on the build machine |
| `pfx` | Certificate is stored as a `.pfx` file |
| `azure` | Using Azure Trusted Signing (cloud-managed, no cert to store) |
| `command` | Any other signing tool |

---

## What to sign: files and package

A `signing:` block has two halves: how packaged files are signed (`files:`) and whether the MSI is signed (`package:`).

```yaml
signing:
  thumbprint: "AAAA..."            # the key for everything, unless package names its own
  timestampUrl: "http://timestamp.digicert.com"
  files:
    mode: ifUnsigned               # always | ifUnsigned | never
  package:
    sign: true                     # the default
```

| `files.mode` | Packaged files signed |
|---|---|
| `ifUnsigned` (default) | Those with no signature yet. Third-party and Microsoft DLLs keep their own signatures. |
| `always` | Every signable file. signtool replaces a file's existing signature, so a vendor's signature is lost. |
| `never` | Only those whose entry says `sign:`. |

`files.mode` is the default for entries that do not say `sign:` (see below). The older words still work: `unsigned` means `ifUnsigned`, `all` means `always`.

The MSI is signed whenever there is a `signing:` block, whatever `files.mode` says. To sign files but not the MSI:

```yaml
signing:
  package:
    sign: false
```

To sign only the MSI, set `files.mode: never`. To keep the block in the file but sign nothing, set both.

With no `signing:` block at all, nothing is signed except entries marked `sign:`, and the MSI is not signed. `--sign-cert` alone turns signing on as if the block were there (`ifUnsigned` copies, then the MSI).

### A different key for the MSI

`package:` can name its own provider, with the same fields as `signing:` (`thumbprint`, `pfx`, `azure`, `command`, `timestampUrl`, `signToolPath`):

```yaml
signing:
  thumbprint: "AAAA..."            # packaged files
  timestampUrl: "http://timestamp.digicert.com"
  package:
    thumbprint: "BBBB..."          # the MSI
```

A `timestampUrl` or `signToolPath` left out of `package:` is taken from `signing:`. With no provider in `package:`, the MSI uses the files' key, including the [automatic choice](#choosing-the-certificate-automatically), which is then made once for both.

Bundles (a signed setup EXE that chains MSIs) are not built yet. When they are, `package:` will cover the bundle EXE too.

## Signing set on entries (`sign:`)

A `structure:` entry or a `groups:` entry can say how its own files are signed, so the choice sits next to the files instead of in a list of patterns:

```yaml
signing:
  files:
    mode: never                  # default for anything not marked

structure:
  - folder: App
    sign: ifUnsigned             # App's files, unless a child says otherwise
    contents:
      - project: src/App/App.csproj
      - source: redist/vendor/*.dll
        sign: never
  - folder: Tools
    sign: always
    contents:
      - source: build/tools/*.exe

groups:
  - id: Help
    destinationDir: '[INSTALLDIR]\Help'
    sign: never
    files:
      - source: docs/*.chm
```

| `sign:` | Effect |
|---|---|
| `always` | Sign every signable file, replacing any existing signature. |
| `ifUnsigned` | Sign files that have no signature yet. |
| `never` | Leave the files alone. |
| (omitted) | Inherit from the parent entry; at the top, `signing.files.mode` decides. |

Rules:

- The nearest setting wins: an entry's `sign:` beats its parent's, and both beat `signing.files.mode`.
- `--sign-files` on the command line overrides every `sign:`.
- `signing.files.include` and `exclude` always apply. Use `exclude` for files that must never be signed, whatever `mode` or `sign:` says.
- A file reached from two entries with different settings is signed with the stronger one (`always`, then `ifUnsigned`, then `never`), and the build warns. Under `--strict` it is an error instead.
- `sign:` works on its own. A config with no `signing:` block signs exactly the entries marked `sign: always` or `sign: ifUnsigned` and leaves the MSI unsigned. The certificate is the one `--sign-cert` names, or else the [automatic choice](#choosing-the-certificate-automatically), so the build fails if the machine has no code-signing certificate or more than one.

## Copies, not sources (`inPlace`)

By default AlliePack does not touch your files. It copies each file it will sign into `obj\AlliePack\<config>\signed\` under the config's folder (`<config>` is the config file's name without extension, so two configs in one folder do not share it), signs the copy, and packages the copy. The folder is emptied at the start of every build and kept afterwards, so a `.wxs` kept with `--keep-wxs` still points at real files.

```yaml
signing:
  inPlace: true      # sign the source files where they are
```

Use `inPlace: true` (or `--sign-in-place`) when the same binaries also ship some other way, such as a zip or a NuGet package, and should be signed there too.

## Exported WXS (`--export-wxs`)

Export never signs. The exported `build.ps1` does, on the machine that builds the MSI, which is where the certificate or token is:

1. Copies the files to sign from their sources into `signed\` in the export folder. The WXS points at those copies. (With `inPlace`, the WXS points at the sources and nothing is copied.)
2. Signs them: the first file on its own, then the rest in batches, stopping at the first failure. `ifUnsigned` files that already have a signature are skipped.
3. Runs `wix build`.
4. Signs the MSI, unless `package.sign` is false.

```powershell
.\build.ps1 -Version 1.2.0.0                                  # sign with the config's certificates
.\build.ps1 -Version 1.2.0.0 -Thumbprint ABCDEF...            # a different certificate
.\build.ps1 -Version 1.2.0.0 -SkipSigning                     # sign in your own pipeline instead
```

| Parameter | Default |
|---|---|
| `-Thumbprint` | `signing.thumbprint` (or `--sign-cert`) at export time. Empty means the only usable code-signing certificate in `CurrentUser\My`. |
| `-TimestampUrl` | `signing.timestampUrl` at export time. |
| `-PackageThumbprint` | `signing.package.thumbprint` at export time. Empty means the MSI uses `-Thumbprint`. |
| `-PackageTimestampUrl` | `signing.package.timestampUrl` at export time. Empty means `-TimestampUrl`. |
| `-SignTool` | Found on `PATH`, then in the Windows SDK. |
| `-SkipSigning` | Copies the files but signs nothing. |

Only the certificate store is supported in `build.ps1`. Where the files' key or the MSI's key is `pfx`, `azure` or `command`, the export warns and `build.ps1` leaves that part unsigned; the rest is still signed.

## Command-line overrides

| Flag | Effect |
|---|---|
| `--sign-files <mode>` | `always`, `ifUnsigned` or `never`. Overrides `signing.files.mode` and every entry's `sign:`. Turns signing on even without a `signing:` block. |
| `--sign-package yes\|no` | Overrides `signing.package.sign`. |
| `--sign-cert <thumbprint>` | Uses this certificate from `CurrentUser\My` for the files and the MSI, instead of the providers in the config. With no `signing:` block, also turns signing on (`ifUnsigned` copies, then the MSI). With a block, the block's `files.mode`, `package.sign` and `inPlace` still apply. |
| `--sign-in-place` | Signs the source files instead of copies. Same as `signing.inPlace: true`. |
| `--no-sign` | Signs nothing. Same as `--sign-files never --sign-package no`. |

Without a `signing:` block, `--sign-files always` or `ifUnsigned` also signs the MSI, and `--sign-package yes` alone signs only the MSI.

```
# A developer build, unsigned, from a config that normally signs:
AlliePack.exe allie-pack.yaml --no-sign

# A release build from a config with no signing: block, using the only cert on the machine:
AlliePack.exe allie-pack.yaml --sign-files ifUnsigned

# Sign only the MSI:
AlliePack.exe allie-pack.yaml --sign-files never
```

`--report` shows the files' default mode, the key for the files, whether the MSI is signed and with which key, and how many files would be signed and how, including which certificate would be picked automatically. Add `--verbose` to list the files.

## Choosing the certificate automatically

With no `thumbprint`, `pfx`, `azure` or `command`, AlliePack looks in `CurrentUser\My` for certificates that have the Code Signing usage, a private key, and a validity period that includes today.

- **Exactly one:** it is used, and the build prints its subject and thumbprint.
- **None, or more than one:** the build stops before anything is signed and lists the candidates. Name one with `signing.thumbprint` or `--sign-cert`.

The test certificate from `tools/New-TestSigningCert.ps1` counts, so a machine that has it and a real certificate has two, and has to name one.

## Hardware tokens (SafeNet eToken and similar)

Since June 2023, new publicly trusted code-signing certificates must keep their private key on hardware: a USB token or a cloud HSM, not a file. With SafeNet Authentication Client (SAC) installed and the token plugged in, the token's certificate appears in `CurrentUser\My`, so the certificate store provider (or the automatic choice above) works. The private key never leaves the token.

```yaml
signing:
  thumbprint: "ABCDEF1234567890ABCDEF1234567890ABCDEF12"   # or omit, if it is the only cert
  timestampUrl: "http://timestamp.digicert.com"
```

SAC asks for the token password in its own window. When AlliePack sees the certificate's key is on a token, it prints a note. What it does to protect the token:

- **The first file is signed on its own.** A wrong password or the wrong certificate fails on one file.
- **The build stops after the first failed `signtool` run.** Tokens lock after a set number of wrong passwords, and continuing would try again for every file. (SAC's own dialog may still let you retry inside one run.)
- **The rest are signed in batches** of many files per `signtool` run, which may mean fewer password prompts, depending on SAC's settings.

Batching applies to the signtool providers (`thumbprint`, `pfx`, `azure`, or the automatic choice). A `command:` provider runs once per file, because `{file}` names one file; it still stops at the first failure.

To be asked once rather than repeatedly, turn on **single logon** in SAC (Client Settings > Advanced > Enable single logon), then log off Windows and back on. Be aware that while you stay logged on, anyone at the machine can then sign without the password.

Unattended signing with a token (the password supplied by the build instead of typed) is not supported yet.

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
  pfx: "$(YamlDir)/certs/MyApp.pfx"
  pfxPassword: "$(SIGN_PASSWORD)"
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
    correlationId: "$(BUILD_ID)"     # optional; shows up in Azure signing logs
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
  command: 'AzureSignTool.exe sign -kvu "$(KV_URL)" -kvc "$(CERT_NAME)" -fd sha256 -tr "http://timestamp.acs.microsoft.com" -td sha256 "{file}"'
```

```
AlliePack.exe allie-pack.yaml
  --define KV_URL=<key-vault-url>
  --define CERT_NAME=<cert-name>
  --output dist\MyApp.msi
```

`{file}` is replaced with the full path of each file being signed (and the MSI itself). Include your own quoting around `{file}` if the tool requires it.

---

## Filtering packaged files (`files:`)

`signing.files` holds filters that limit which files may be signed at all, whatever `files.mode` or an entry's `sign:` says:

```yaml
signing:
  thumbprint: "ABCDEF1234567890ABCDEF1234567890ABCDEF12"
  timestampUrl: "http://timestamp.digicert.com"
  files:
    include: ["*.exe", "*.dll"]
    exclude: ["*.resources.dll"]    # satellite assemblies are typically skipped
```

`include` is a list of filename glob patterns (not paths). When omitted, AlliePack uses the Windows SIP registry to determine which files are signable, the same check `signtool` performs internally. Files without a registered SIP handler (text files, XML, images) are skipped regardless.

`exclude` is applied after `include` (or the SIP check).

`files.mode` sits in the same block; see [What to sign](#what-to-sign-files-and-package).

### Diagnostic output

AlliePack prints a status line for every file during the signing step:

```
Signing packaged files (copies in obj\AlliePack\<config>\signed)...
  [signed  ] MyApp.exe
  [signed  ] MyLib.dll
  [skip    ] notepad.exe  -- already signed
  files: 2 signed, 1 already signed, 1 not signable
  signed : dist\MyApp.msi
```

Pass `--verbose` to also see files that were skipped for any reason.

A failure stops the step at once; the summary gives an upper bound on how many files were not signed (signtool may have signed some of a failed batch). A file that appears under two destinations is signed once.

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
