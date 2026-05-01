# Cookbook

How-to recipes for common AlliePack tasks. Each recipe is self-contained.

---

## Adding a Visual Studio project

Use the `project:` key to include a single `.csproj` or `.vbproj` output without knowing the output directory:

```yaml
aliases:
  src: "[GitRoot]/src"

structure:
  - project: "[src]/MyApp/MyApp.csproj"
    configuration: Release
    platform: x64
    excludeFiles:
      - "*.pdb"
      - "*.xml"
      - "*.deps.json"
```

AlliePack reads the project file, infers the build output directory, and includes all files from it. You do not need to list files individually or hardcode `bin/Release/net481`.

If you need a specific `platform` value, use exactly the string MSBuild uses — `x64`, `x86`, or `AnyCPU` (note: no space, unlike Visual Studio's "Any CPU" display).

---

## Adding a Visual Studio solution

Use `solution:` to include outputs from every project in a `.sln`. This is the simplest way to package a multi-project application:

```yaml
structure:
  - solution: "[GitRoot]/MyApp.sln"
    configuration: Release
    excludeProjects:
      - "MyApp.Tests"
      - "MyApp.IntegrationTests"
      - "MyApp.Benchmarks"
    excludeFiles:
      - "*.pdb"
      - "*.xml"
```

AlliePack walks every project in the solution, resolves each output directory, and collects all files. `excludeProjects` filters by project name (not path). Test and benchmark projects are typically the only ones to exclude.

---

## Using glob patterns

**All files of a type:**
```yaml
- source: "bin:*.dll"
```

**Multiple types at once:** use separate entries:
```yaml
- source: "bin:*.dll"
- source: "bin:*.exe"
```

**Recursive (preserve subdirectory structure):**
```yaml
- folder: "Help"
  contents:
    - source: "help:**/*"
```

If `help/` contains `images/logo.png` and `pages/intro.html`, they install at `INSTALLDIR\Help\images\logo.png` and `INSTALLDIR\Help\pages\intro.html`.

**Exclude specific patterns:** combine a wide include with targeted excludes:
```yaml
- source: "bin:**/*"
  excludeFiles:
    - "*.pdb"
    - "*.xml"
    - "*.config.json"
    - "bin/**/*"      # exclude nested bin directories from recursive results
    - "obj/**/*"
```

---

## Overriding paths for CI

The standard pattern: use `[CurrentDir]` as a default in `paths:`, then override it in CI with `--define`:

```yaml
paths:
  srcRoot: "[CurrentDir]"     # works locally when run from the source directory

aliases:
  bin: "[srcRoot]/src/MyApp/bin/Release/net481"
```

**Local build:**
```
cd C:\src\MyApp
AlliePack allie-pack.yaml
```

**CI build (Azure DevOps):**
```yaml
- script: |
    AlliePack.exe allie-pack.yaml
      --define srcRoot=$(Build.SourcesDirectory)
      --output $(Build.ArtifactStagingDirectory)\MyApp.msi
  displayName: Build MSI
```

`--define` always wins over `paths:`, so you never need to edit the YAML for CI.

---

## Injecting a version number at build time

Use a `[VERSION]` token in the config and pass the actual value from CI:

```yaml
product:
  name: "My App"
  version: "[VERSION]"
```

```
AlliePack.exe allie-pack.yaml --define VERSION=2.1.0 --output dist\MyApp-2.1.0.msi
```

In Azure DevOps, use the build number:

```yaml
- script: AlliePack.exe allie-pack.yaml --define VERSION=$(Build.BuildNumber) --output dist\MyApp.msi
```

---

## Version from git tags

AlliePack can derive the version directly from your git tags, producing `Major.Minor.Patch.CommitCount`:

```yaml
product:
  version:
    source: "git-tag"
    tagPrefix: "v"    # matches tags like v1.2.3
```

Tag `v1.2.3` with 0 commits since the tag → `1.2.3.0`.
Tag `v1.2.3` with 7 commits since → `1.2.3.7`.
No matching tag → `0.0.0.<total commits>`.

---

## Version from a built executable

Read the version from a PE file's `FileVersionInfo` rather than specifying it manually:

```yaml
product:
  version:
    file: "bin:MyApp.exe"
    source: "file-version"    # or "product-version"
```

The executable must be built before AlliePack runs.

---

## Signing the MSI

Add a `signing:` block with your preferred provider. The MSI is signed after WiX builds it.

**Certificate store (thumbprint):** best for automated builds where the cert is installed on the build machine:

```yaml
signing:
  thumbprint: "ABCDEF1234567890ABCDEF1234567890ABCDEF12"
  timestampUrl: "http://timestamp.digicert.com"
```

**PFX file:** useful when the certificate is stored as a file in a secure location:

```yaml
signing:
  pfx: "[YamlDir]/certs/MyApp.pfx"
  pfxPassword: "[SIGN_PASSWORD]"
  timestampUrl: "http://timestamp.digicert.com"
```

```
AlliePack.exe allie-pack.yaml --define SIGN_PASSWORD=$(SIGN_PASSWORD)
```

**Azure Trusted Signing:**

```yaml
signing:
  azure:
    endpoint: "https://eus.codesigning.azure.net"
    account: "MySigningAccount"
    certificateProfile: "MyProfile"
    dlibPath: '[DLIB_PATH]'
  timestampUrl: "http://timestamp.acs.microsoft.com"
```

See [Signing](signing.md) for the full guide including per-file signing and local testing.

---

## Signing packaged files before packaging

To sign `.exe` and `.dll` files before WiX bundles them into the MSI, add a `files:` subsection:

```yaml
signing:
  thumbprint: "ABCDEF1234567890ABCDEF1234567890ABCDEF12"
  timestampUrl: "http://timestamp.digicert.com"
  files:
    mode: unsigned                  # skip files that are already signed
    include: ["*.exe", "*.dll"]
    exclude: ["*.resources.dll"]    # skip satellite assemblies
```

The signing order is always: sign files → build MSI → sign MSI.

---

## Creating a Start Menu shortcut

```yaml
shortcuts:
  - name: "My App"
    target: "[INSTALLDIR]\\MyApp.exe"
    description: "Launch My App"
    folder: "[ProgramMenuFolder]"
```

For a scope-aware shortcut that works correctly with `installScope: both`:

```yaml
shortcuts:
  - name: "My App"
    target: "[INSTALLDIR]\\MyApp.exe"
    folder: startmenu    # resolves to per-user or all-users Start Menu based on scope
```

---

## Setting environment variables

```yaml
environment:
  - name: "MYAPP_HOME"
    value: "[INSTALLDIR]"
    scope: machine       # machine or user

  - name: "PATH"
    value: "[INSTALLDIR]\\bin"
    scope: machine
```

Variables are removed on uninstall. For scope-aware configs:

```yaml
environment:
  - name: "MYAPP_HOME"
    value: "[INSTALLDIR]"
    scope:
      PerUser:    user
      PerMachine: machine
      _else:      user
```

---

## Writing registry values

```yaml
registry:
  - root: HKLM
    key: "SOFTWARE\\MyCompany\\MyApp"
    name: "InstallDir"
    value: "[INSTALLDIR]"
    type: string

  - root: HKLM
    key: "SOFTWARE\\MyCompany\\MyApp"
    name: "Version"
    value: "1.0.0.0"
    type: string

  - root: HKLM
    key: "SOFTWARE\\MyCompany\\MyApp"
    name: "FeatureLevel"
    value: "3"
    type: dword
```

For a 32-bit entry in a 64-bit installer (e.g., COM interop registration):

```yaml
- root: HKLM
  key: "SOFTWARE\\MyCompany\\MyApp"
  name: "ComPath"
  value: "[INSTALLDIR]\\MyApp.ComServer.dll"
  win64: false    # write to the 32-bit WOW64 view
```

---

## Installing a Windows service

```yaml
services:
  - name: "MyAppWorker"
    displayName: "My App Background Worker"
    description: "Processes background jobs for My App"
    executable: "[INSTALLDIR]\\MyAppWorker.exe"
    start: auto
    account: LocalSystem
    onFailure:
      first: restart
      second: restart
      third: none
      restartDelaySeconds: 60
      resetAfterDays: 1
```

For a service that should only start after another service is running:

```yaml
- name: "MyAppApi"
  executable: "[INSTALLDIR]\\MyAppApi.exe"
  start: auto
  dependsOn:
    - "MyAppWorker"
```

---

## Installing files outside INSTALLDIR

Use `directories:` to define named destinations and `groups:` to assign files to them:

```yaml
directories:
  - id: CONFIGDIR
    type: config           # AppData (per-user) or ProgramData (per-machine)
    subPath: "MyCompany\\MyApp"

groups:
  - id: DefaultConfig
    destinationDir: CONFIGDIR
    condition: notExists   # only install if file doesn't already exist yet
    files:
      - source: "[YamlDir]/installer/defaults.config"
        rename: "myapp.config"
```

`condition: notExists` ensures upgrades don't overwrite the user's customized config file.

---

## Installing a PowerShell module

```yaml
directories:
  - id: PSMOD
    type: psmodules51
    subPath: "MyApp"

groups:
  - id: PowerShellModule
    destinationDir: PSMOD
    files:
      - source: "scripts:MyApp.psm1"
      - source: "scripts:MyApp.psd1"
```

For PowerShell 7+, use `type: psmodules7` instead.

---

## Per-user and per-machine builds from one config

Use `installScope: both` for the simplest case — AlliePack infers sensible path defaults:

```yaml
product:
  name: "My App"
  installScope: both
```

```
# Build both targets:
AlliePack.exe allie-pack.yaml --scope perUser    --output dist\MyApp-user.msi
AlliePack.exe allie-pack.yaml --scope perMachine --output dist\MyApp-machine.msi
```

For finer control, use release flags with conditional maps:

```yaml
releaseFlags:
  - PerUser
  - PerMachine
defaultActiveFlags:
  - PerUser

product:
  installScope:
    PerUser:    perUser
    PerMachine: perMachine
    _else:      perUser

environment:
  - name: "MYAPP_HOME"
    value: "[INSTALLDIR]"
    scope:
      PerUser:    user
      PerMachine: machine
      _else:      user
```

```
AlliePack.exe allie-pack.yaml --flag PerMachine --output dist\MyApp-machine.msi
```

---

## Optional selectable features

Show users a feature selection screen in the installer UI:

```yaml
features:
  - id: MainApp
    name: "Application"
    default: true
    structure:
      - source: "bin:MyApp.exe"
      - source: "bin:*.dll"
    shortcuts:
      - name: "My App"
        target: "[INSTALLDIR]\\MyApp.exe"
        folder: startmenu

  - id: CliTools
    name: "Command-line Tools"
    description: "Installs MyApp CLI tools for scripting and automation"
    default: false
    structure:
      - folder: "cli"
        contents:
          - source: "bin:myapp-cli.exe"

  - id: PowerShellModule
    name: "PowerShell Integration"
    description: "Adds PowerShell cmdlets for My App"
    default: false
    groups:
      - id: PSMod
        destinationDir: PSMOD
        files:
          - source: "scripts:MyApp.psm1"
```

Top-level `structure:`, `registry:`, `services:`, etc. are always installed. Features contain the optional, user-selectable content.

---

## Adding a license agreement dialog

```yaml
product:
  licenseFile: "[YamlDir]/license.rtf"
```

The file must be in `.rtf` format. AlliePack adds a license agreement dialog to the installer UI automatically.

---

## Pinning a specific WiX version

If multiple WiX versions are installed, pin to one in the config:

```yaml
wixToolsPath: "C:/tools/wix-5.0.2/bin"
```

Or without changing the YAML, set the environment variable before running:

```
set WIXSHARP_WIXLOCATION=C:\tools\wix-5.0.2\bin
AlliePack.exe allie-pack.yaml
```

---

## Inspecting the generated WiX XML

AlliePack generates a `.wxs` file and passes it to WiX. Use `--keep-wxs` to see what was generated:

```
AlliePack.exe allie-pack.yaml --keep-wxs
```

The `.wxs` file is written alongside the output MSI. This is useful when debugging an issue or writing a `wix.fragments:` entry that needs to reference element IDs in the generated document.

---

## Injecting raw WiX XML

For installer requirements AlliePack doesn't cover natively, add `wix.fragments:`:

```yaml
wix:
  fragments:
    - inline: |
        <Fragment>
          <Property Id="ARPNOMODIFY" Value="1" />
          <Property Id="ARPNOREPAIR" Value="1" />
        </Fragment>
```

This example disables the Modify and Repair buttons in Add/Remove Programs, which is common for applications that use their own update mechanism.

---

## Validating your config without building

Run `--report` any time you want to check your config without invoking the WiX compiler:

```
AlliePack.exe allie-pack.yaml --report
```

This is fast and shows you exactly what will be included, what paths resolved to, and what the signing configuration looks like. It's the fastest way to catch file resolution problems.
