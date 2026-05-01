# AlliePack

AlliePack is a YAML-driven MSI installer builder built on **WixSharp** and **WiX v5**. Define your installer in a human-readable `allie-pack.yaml` file instead of writing complex WiX XML or C# scripts.

## Features

- **YAML configuration** for product info, file structure, and shortcuts
- **Path resolution** with built-in tokens (`[YamlDir]`, `[GitRoot]`, `[CurrentDir]`) and user-defined aliases
- **Glob pattern** support for bulk file inclusion (e.g., `bin:*.dll`)
- **Visual Studio integration** -- resolve build outputs directly from `.sln` or `.csproj` files
- **Shortcut creation** for Start Menu, Desktop, or any WiX folder property
- **WPF managed UI** with standard installer dialogs (Welcome, InstallDir, Progress, Exit); optional license agreement dialog
- **Token substitution** via `--define KEY=VALUE` for injecting version numbers, paths, or any value into YAML at build time
- **Flexible versioning** -- literal string, PE file version (FileVersionInfo), or derived from git tags
- **Dry-run / report mode** to preview MSI contents without building
- **Platform support**: x86, x64, arm64
- **Install scope**: perMachine, perUser, or both (with flag-driven selection)
- **Registry keys and values**
- **Environment variables** (set/remove on install/uninstall)
- **Windows services** (install, start, stop, remove)
- **Code signing** -- sign the MSI and packaged files with `signtool.exe`; supports cert store (thumbprint), PFX, Azure Trusted Signing, or any custom signing tool
- **Release flags** for conditional configuration (perUser vs. perMachine builds, etc.)

## Prerequisites

- .NET Framework 4.8.1
- WiX Toolset v5 installed as a dotnet tool:
  ```
  dotnet tool install --global wix --version 5.*
  ```

## Usage

```
AlliePack.exe [config] [options]

Arguments:
  config                    Path to a config file, or a directory containing
                            allie-pack.yaml. Omit to use allie-pack.yaml in
                            the current directory.

Options:
  -r, --report              Preview resolved files without building the MSI
  -o, --output <path>       Output path for the generated MSI
  -v, --verbose             Enable verbose output
  -D, --define KEY=VALUE    Override a named token at resolution time.
                            Repeatable: -D srcRoot=D:\src -D EDITION=Pro
                            Defines overlay the paths: block; command-line wins.
      --flag <name>         Active release flag. Selects values from conditional
                            maps in the config. Falls back to defaultActiveFlags,
                            then to unconditional values.
      --scope <value>       Override install scope for 'installScope: both'
                            configs. Values: perUser, perMachine.
      --keep-wxs            Preserve the generated .wxs source file after
                            building. Useful for debugging or CI artifacts.
```

### Examples

```
# Build from allie-pack.yaml in the current directory
AlliePack.exe

# Build from an explicit config file
AlliePack.exe allie-pack.yaml

# Preview what will be in the MSI without building
AlliePack.exe allie-pack.yaml --report

# Inject a version at build time
AlliePack.exe allie-pack.yaml -D VERSION=2.1.0

# Write MSI to a specific location
AlliePack.exe allie-pack.yaml --output C:\builds\MyApp-2.1.0.msi

# Build a per-machine variant using release flags
AlliePack.exe allie-pack.yaml --flag PerMachine

# Retain the generated WiX source for inspection
AlliePack.exe allie-pack.yaml --keep-wxs
```

## Environment Variables

| Variable | Purpose |
|---|---|
| `WIXSHARP_WIXLOCATION` | Path to the directory containing `wix.exe`. Overrides PATH discovery. Use in CI environments where multiple WiX versions are installed. |
| `ALLIEPAK_KEEP_WXS` | Set to any value to preserve the generated `.wxs` file after building. Equivalent to `--keep-wxs`. |

## Config File Naming

| Scenario | Filename |
|---|---|
| Default file in a project directory | `allie-pack.yaml` |
| Named/project-specific config | `<project>.allie.yaml` (e.g., `myapp.allie.yaml`) |

When no config argument is given, AlliePack looks for `allie-pack.yaml` in the current directory. Passing a directory path does the same. Named configs must be referenced explicitly.

## Configuration Reference

### `product`

```yaml
product:
  name: "My App"
  manufacturer: "My Company"
  version: "1.0.0.0"         # See version sourcing options below
  description: "My App installer"
  upgradeCode: "YOUR-GUID"   # Fixed GUID -- changing this breaks upgrades
  installScope: "perMachine" # perMachine, perUser, or both
  platform: "x64"            # x86 (default), x64, arm64
  installDir: "[ProgramFiles]\\MyCompany\\MyApp"  # optional custom install path
  licenseFile: "license.rtf" # optional; adds license agreement dialog
```

#### Version sourcing

**Literal string:**
```yaml
version: "1.0.0.0"
```

**From a PE file (FileVersionInfo):**
```yaml
version:
  file: "bin:MyApp.exe"
  source: "file-version"     # or "product-version"
```

**From git tags** (derives `Major.Minor.Patch.CommitCount` from the nearest tag):
```yaml
version:
  source: "git-tag"
  tagPrefix: "v"             # optional; defaults to "v"
```

With git-tag sourcing, a tag of `v1.2.3` with 7 commits since the tag produces version `1.2.3.7`. If no matching tag exists, falls back to `0.0.0.<total-commits>`.

### `paths`

Named path tokens for this config. Values may use built-in tokens. Use `--define`
on the command line to override them — useful for CI where paths differ from local
development without changing the YAML file.

```yaml
paths:
  srcRoot: "[CurrentDir]"          # default to CWD; override in CI with --define srcRoot=...
  assets:  "[YamlDir]/../assets"
```

Tokens defined here are used anywhere in the config as `[name]`:

```yaml
structure:
  - project: "[srcRoot]/MyApp/MyApp.csproj"
    configuration: Release
```

**CI override pattern** — no changes to the YAML needed between environments:

```
# Local: run AlliePack from the source directory; [CurrentDir] resolves automatically.
cd C:\src\MyApp && AlliePack path\to\allie-pack.yaml

# CI: working directory is not the source root, so pass it explicitly.
AlliePack allie-pack.yaml --define srcRoot=$(Build.SourcesDirectory)\src
```

### `aliases`

Aliases are short names for paths, used in `source:` fields with the `alias:path` syntax. Token substitution applies inside alias values.

```yaml
aliases:
  bin: "[GitRoot]/src/MyApp/bin/Release/net481"
  assets: "resources/dist"
```

Built-in path tokens:

| Token | Resolves to |
|---|---|
| `[YamlDir]` | Directory containing the YAML config file |
| `[GitRoot]` | Root of the nearest git repository |
| `[CurrentDir]` | Current working directory when AlliePack is invoked |

### `structure`

Defines the folder and file hierarchy that will be installed.

```yaml
structure:
  - folder: "App"
    contents:
      - source: "bin:MyApp.exe"
      - source: "bin:*.dll"
        excludeFiles:
          - "*.pdb"
          - "*.xml"

  - folder: "Plugins"
    contents:
      # Include all build outputs from a solution
      - solution: "src/MyApp.sln"
        configuration: "Release"
        platform: "Any CPU"
        excludeProjects:
          - "MyApp.Tests"
        excludeFiles:
          - "*.pdb"

      # Or from a single project
      - project: "src/MyApp.Core/MyApp.Core.csproj"
        configuration: "Release"

      # Nested folders
      - folder: "Data"
        contents:
          - source: "assets:*.json"
```

**Source field syntax:**
- `alias:pattern` -- files matching `pattern` under the alias path
- `[Token]/path/pattern` -- token-based path with optional glob
- Relative paths are resolved from the YAML file's directory
- `**` globs preserve subdirectory structure

### `shortcuts`

```yaml
shortcuts:
  - name: "My App"
    target: "[INSTALLDIR]\\MyApp.exe"
    description: "Launch My App"
    folder: "[ProgramMenuFolder]"

  - name: "My App"
    target: "[INSTALLDIR]\\MyApp.exe"
    folder: "[Desktop]"
```

**Folder values:**

| YAML value | Location |
|---|---|
| `[ProgramMenuFolder]` | Start Menu\Programs (per-user) |
| `[CommonProgramMenuFolder]` | Start Menu\Programs (all users) |
| `[Desktop]` / `[DesktopFolder]` | Desktop (per-user) |
| `[CommonDesktopFolder]` | Desktop (all users) |
| `startmenu` / `desktop` / `startup` | Scope-aware alias (resolves per- or all-users based on installScope) |

### `environment`

Set user or machine environment variables during installation. Variables are removed on uninstall.

```yaml
environment:
  - name: "MYAPP_HOME"
    value: "[INSTALLDIR]"
    scope: user       # user (default) or machine

  - name: "PATH"
    value: "[INSTALLDIR]\\bin"
    scope: machine
```

### `registry`

```yaml
registry:
  - root: "HKLM"
    key: "SOFTWARE\\MyCompany\\MyApp"
    name: "InstallDir"
    value: "[INSTALLDIR]"
    type: string      # string (default), expandString, multiString, dword, qword, binary
    win64: true       # optional; defaults to platform setting
```

### `services`

```yaml
services:
  - name: "MyAppService"
    displayName: "My App Service"
    description: "Background service for My App"
    executable: "MyAppService.exe"
    start: auto       # auto, manual, disabled
    account: "LocalSystem"
    onFailure:
      first: restart
      second: restart
      third: none
      restartDelaySeconds: 60
      resetAfterDays: 1
```

### `directories` and `groups`

Install files to locations outside `INSTALLDIR`.

```yaml
directories:
  - id: PSMODDIR
    path: "[PersonalFolder]\\WindowsPowerShell\\Modules\\MyApp"

  - id: CONFIGDIR
    type: config      # config, localdata, desktop, startmenu, startup,
    subPath: "MyCompany\\MyApp"   # psmodules51, psmodules7

groups:
  - id: PsModule
    destinationDir: PSMODDIR
    files:
      - source: "scripts:MyApp.psm1"

  - id: DefaultConfig
    destinationDir: CONFIGDIR
    condition: notExists   # only install if file is not already present
    files:
      - source: "installer/myapp.config.ini"
        rename: "config.ini"
```

### `wixToolsPath`

Pin AlliePack to a specific WiX installation, bypassing PATH discovery. Useful in CI environments where multiple WiX versions are installed.

```yaml
wixToolsPath: "C:/tools/wix5/bin"
```

Can also be set via the `WIXSHARP_WIXLOCATION` environment variable (takes effect when `wixToolsPath` is not set in the config).

### `signing`

Sign the built MSI (and optionally the packaged files) with `signtool.exe`.
Exactly one signing provider is required; the rest of the config is unaffected
when this block is omitted.

See **[docs/signing.md](docs/signing.md)** for the full reference including
Azure Trusted Signing, per-file signing, diagnostic output, and local testing.

**Certificate store (thumbprint):**
```yaml
signing:
  thumbprint: "ABCDEF1234567890ABCDEF1234567890ABCDEF12"
  timestampUrl: "http://timestamp.digicert.com"
```

**PFX file** (inject password at build time):
```yaml
signing:
  pfx: "certs/MyApp.pfx"
  pfxPassword: "[SIGN_PASSWORD]"    # --define SIGN_PASSWORD=$(SIGN_PASSWORD)
  timestampUrl: "http://timestamp.digicert.com"
```

**Azure Trusted Signing:**
```yaml
signing:
  azure:
    endpoint: "https://eus.codesigning.azure.net"
    account: "MySigningAccount"
    certificateProfile: "MyProfile"
    dlibPath: 'C:\Tools\x64\Azure.CodeSigning.Dlib.dll'
  timestampUrl: "http://timestamp.acs.microsoft.com"
```

**Sign packaged files before WiX packages them** (add a `files:` subsection to any provider):
```yaml
signing:
  thumbprint: "ABCDEF1234..."
  timestampUrl: "http://timestamp.digicert.com"
  files:
    mode: unsigned                  # skip files already signed (default)
    include: ["*.exe", "*.dll"]
    exclude: ["*.resources.dll"]
```

### `wix`

Raw WiX XML escape hatch for anything AlliePack doesn't cover natively.

```yaml
wix:
  fragments:
    - inline: |
        <Fragment>
          <Property Id="MYKEY" Value="1" />
        </Fragment>
    - file: "installer/extra.wxs"
```

## Project Structure

```
src/AlliePack/
  Program.cs            Entry point and CLI argument handling
  Options.cs            Command-line argument definitions
  ConfigModels.cs       YAML deserialization models
  PathResolver.cs       Token, alias, and glob path resolution
  SolutionResolver.cs   Visual Studio solution and project output detection
  ResolvedFile.cs       Source-to-destination file mapping model
  InstallerBuilder.cs   WixSharp project construction and MSI generation
```

## Roadmap

- [x] YAML-driven configuration
- [x] Path resolution (tokens, aliases, globs)
- [x] Visual Studio solution and project output resolution
- [x] Shortcut creation
- [x] WPF managed UI with standard dialogs
- [x] License agreement dialog
- [x] `--define` token substitution
- [x] x64 / arm64 platform support
- [x] Recursive `**` glob source (preserves subdirectory structure)
- [x] Registry keys and values
- [x] Environment variables
- [x] Windows services
- [x] Named directories and file groups (install outside INSTALLDIR)
- [x] Release flags and conditional inclusion (`--flag PerUser` / `--flag PerMachine`)
- [x] `condition: notExists` -- install config files on first install only
- [x] Flexible version sourcing (literal, file, git-tag)
- [x] Raw WiX XML escape hatch (`wix.fragments`)
- [x] Code signing (MSI + per-file; thumbprint, PFX, Azure Trusted Signing, custom command)
- [ ] Optional installer features (component selection)
- [ ] Modular YAML includes

## License

MIT Licensed.
