# CI/CD Integration

Integrating AlliePack into a pipeline follows the same pattern regardless of the system: install prerequisites, run AlliePack with appropriate `--define` overrides for CI paths and secrets, publish the resulting MSI as an artifact.

---

## Azure DevOps

### Basic MSI build

```yaml
trigger:
  - main

pool:
  vmImage: windows-latest

steps:
- task: UseDotNet@2
  displayName: Install .NET SDK (for WiX tool install)
  inputs:
    version: '8.x'

- script: dotnet tool install --global wix --version 5.*
  displayName: Install WiX v5

- script: |
    AlliePack.exe allie-pack.yaml
      --define srcRoot=$(Build.SourcesDirectory)
      --define VERSION=$(Build.BuildNumber)
      --output $(Build.ArtifactStagingDirectory)\MyApp.msi
  displayName: Build MSI

- task: PublishBuildArtifacts@1
  displayName: Publish MSI artifact
  inputs:
    pathToPublish: $(Build.ArtifactStagingDirectory)
    artifactName: installer
```

If AlliePack.exe is in your repository (or downloaded as a pipeline artifact), reference it directly. Otherwise, add a download step first.

### Signing with a thumbprint certificate

Install the certificate first, then reference its thumbprint. The thumbprint is stable — it doesn't change when the cert is renewed.

```yaml
- task: PowerShell@2
  displayName: Install signing certificate
  inputs:
    targetType: inline
    script: |
      $pfxBytes = [System.Convert]::FromBase64String('$(SIGNING_CERT_BASE64)')
      $pfxPath  = Join-Path $env:TEMP 'sign.pfx'
      [System.IO.File]::WriteAllBytes($pfxPath, $pfxBytes)
      Import-PfxCertificate -FilePath $pfxPath -CertStoreLocation Cert:\CurrentUser\My -Password (ConvertTo-SecureString '$(SIGNING_CERT_PASSWORD)' -AsPlainText -Force)
      Remove-Item $pfxPath
  env:
    SIGNING_CERT_BASE64: $(SIGNING_CERT_BASE64)
    SIGNING_CERT_PASSWORD: $(SIGNING_CERT_PASSWORD)

- script: |
    AlliePack.exe allie-pack.yaml
      --define srcRoot=$(Build.SourcesDirectory)
      --define VERSION=$(Build.BuildNumber)
      --output $(Build.ArtifactStagingDirectory)\MyApp.msi
  displayName: Build and sign MSI
```

The `signing.thumbprint` in your YAML refers to the cert you just imported. Pipeline variables `SIGNING_CERT_BASE64` and `SIGNING_CERT_PASSWORD` should be marked as secret.

### Signing with a PFX file

Store the PFX as a pipeline Secure File, download it, and pass the path via `--define`:

```yaml
- task: DownloadSecureFile@1
  displayName: Download signing certificate
  name: signingCert
  inputs:
    secureFile: MyApp.pfx

- script: |
    AlliePack.exe allie-pack.yaml
      --define srcRoot=$(Build.SourcesDirectory)
      --define VERSION=$(Build.BuildNumber)
      --define CERT_PATH=$(signingCert.secureFilePath)
      --define SIGN_PASSWORD=$(SIGN_PASSWORD)
      --output $(Build.ArtifactStagingDirectory)\MyApp.msi
  displayName: Build and sign MSI
  env:
    SIGN_PASSWORD: $(SIGN_PASSWORD)
```

Your YAML would have:
```yaml
signing:
  pfx: "[CERT_PATH]"
  pfxPassword: "[SIGN_PASSWORD]"
  timestampUrl: "http://timestamp.digicert.com"
```

### Signing with Azure Trusted Signing

Azure Trusted Signing uses the pipeline's managed identity for authentication — no secrets to manage.

```yaml
- script: winget install -e --id Microsoft.Azure.ArtifactSigningClientTools
  displayName: Install Azure Trusted Signing client tools

- script: |
    AlliePack.exe allie-pack.yaml
      --define srcRoot=$(Build.SourcesDirectory)
      --define VERSION=$(Build.BuildNumber)
      --define BUILD_ID=$(Build.BuildId)
      --output $(Build.ArtifactStagingDirectory)\MyApp.msi
  displayName: Build and sign MSI (Azure Trusted Signing)
```

Your YAML would have:
```yaml
signing:
  azure:
    endpoint: "https://eus.codesigning.azure.net"
    account: "MySigningAccount"
    certificateProfile: "MyProfile"
    dlibPath: 'C:\Program Files\Microsoft\Azure Code Signing\x64\Azure.CodeSigning.Dlib.dll'
    correlationId: "[BUILD_ID]"
  timestampUrl: "http://timestamp.acs.microsoft.com"
```

### Building per-user and per-machine MSIs

```yaml
- script: |
    AlliePack.exe allie-pack.yaml
      --define srcRoot=$(Build.SourcesDirectory)
      --define VERSION=$(Build.BuildNumber)
      --scope perUser
      --output $(Build.ArtifactStagingDirectory)\MyApp-user.msi
  displayName: Build per-user MSI

- script: |
    AlliePack.exe allie-pack.yaml
      --define srcRoot=$(Build.SourcesDirectory)
      --define VERSION=$(Build.BuildNumber)
      --scope perMachine
      --output $(Build.ArtifactStagingDirectory)\MyApp-machine.msi
  displayName: Build per-machine MSI
```

---

## GitHub Actions

### Basic MSI build

```yaml
name: Build MSI

on:
  push:
    branches: [main]

jobs:
  build:
    runs-on: windows-latest

    steps:
    - uses: actions/checkout@v4

    - name: Install WiX v5
      run: dotnet tool install --global wix --version 5.*

    - name: Build MSI
      run: |
        AlliePack.exe allie-pack.yaml `
          --define srcRoot=${{ github.workspace }} `
          --define VERSION=${{ github.run_number }} `
          --output dist\MyApp.msi

    - name: Upload MSI artifact
      uses: actions/upload-artifact@v4
      with:
        name: installer
        path: dist\MyApp.msi
```

### Signing with a PFX stored as a secret

Store the base64-encoded PFX as a repository secret (`SIGNING_CERT_BASE64`) and the password as `SIGNING_CERT_PASSWORD`.

```yaml
    - name: Decode signing certificate
      run: |
        $pfxBytes = [System.Convert]::FromBase64String('${{ secrets.SIGNING_CERT_BASE64 }}')
        [System.IO.File]::WriteAllBytes('${{ runner.temp }}\sign.pfx', $pfxBytes)
      shell: pwsh

    - name: Build and sign MSI
      run: |
        AlliePack.exe allie-pack.yaml `
          --define srcRoot=${{ github.workspace }} `
          --define VERSION=${{ github.run_number }} `
          --define CERT_PATH=${{ runner.temp }}\sign.pfx `
          --define SIGN_PASSWORD=${{ secrets.SIGNING_CERT_PASSWORD }} `
          --output dist\MyApp.msi
      shell: pwsh
```

Your YAML:
```yaml
signing:
  pfx: "[CERT_PATH]"
  pfxPassword: "[SIGN_PASSWORD]"
  timestampUrl: "http://timestamp.digicert.com"
```

### Signing with Azure Trusted Signing

Use the `azure/trusted-signing-action` or pass credentials via environment variables to the `azure` provider. For managed identity (Azure-hosted runners), the credentials are picked up automatically:

```yaml
    - name: Install Azure Trusted Signing client tools
      run: winget install -e --id Microsoft.Azure.ArtifactSigningClientTools

    - name: Build and sign MSI
      run: |
        AlliePack.exe allie-pack.yaml `
          --define srcRoot=${{ github.workspace }} `
          --define VERSION=${{ github.run_number }} `
          --define BUILD_ID=${{ github.run_id }} `
          --output dist\MyApp.msi
      env:
        AZURE_CLIENT_ID: ${{ secrets.AZURE_CLIENT_ID }}
        AZURE_TENANT_ID: ${{ secrets.AZURE_TENANT_ID }}
        AZURE_CLIENT_SECRET: ${{ secrets.AZURE_CLIENT_SECRET }}
```

---

## Best practices for CI

**Never put secrets in allie-pack.yaml.** Signing passwords, certificate paths, and access tokens should always be passed via `--define` from pipeline secret variables. The YAML file should be safe to commit to source control.

**Use `[CurrentDir]` or `[GitRoot]` for local builds; override with `--define` for CI.** This pattern means the YAML works correctly both locally and in CI without modification:

```yaml
paths:
  srcRoot: "[GitRoot]"     # works locally
```

```
# CI override:
--define srcRoot=$(Build.SourcesDirectory)
```

**Use `--report` to validate before the build step.** Run `--report` as a separate step to catch path resolution errors cheaply, before invoking the WiX compiler:

```yaml
- script: AlliePack.exe allie-pack.yaml --define srcRoot=$(Build.SourcesDirectory) --report
  displayName: Validate config

- script: AlliePack.exe allie-pack.yaml --define srcRoot=$(Build.SourcesDirectory) --output dist\MyApp.msi
  displayName: Build MSI
```

**Keep the generated `.wxs` file in CI as a build artifact** when debugging WiX issues:

```yaml
- script: AlliePack.exe allie-pack.yaml --keep-wxs --output dist\MyApp.msi
  displayName: Build MSI

- task: PublishBuildArtifacts@1
  inputs:
    pathToPublish: dist
    artifactName: installer-debug
  condition: failed()    # only publish on failure for diagnostics
```

**Pin the WiX version** in CI to avoid unexpected behavior when WiX releases a new version:

```
dotnet tool install --global wix --version 5.0.2
```
