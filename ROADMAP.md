# AlliePack Roadmap

This document tracks planned features and their implementation priority. Items are grouped into phases based on dependency order and complexity. The [AlliePack-docs](https://github.com/seraphire/AlliePack-docs) repository contains detailed design specifications and YAML schema examples for many of these features.

## Design Principle: Progressive Complexity

AlliePack's core promise is that a simple installer stays simple. A minimal working
config is a dozen lines of YAML. Every feature added beyond that should be invisible
to users who don't need it -- no required fields, no forced migration, no new concepts
that appear in someone else's config and demand explanation.

**Rules for new features:**
- Zero-config defaults: the feature must work sensibly without the user knowing it exists
- New sections over nested keys: a new top-level block (like `winget:`, `features:`) is
  less intimidating than adding more keys to `product:` -- if you don't need it, skip it
- No new required fields -- everything has a sane default or is omitted entirely
- An advanced developer should be able to find and use every capability; an average
  developer should never be forced to read past what they need

The YAML structure itself communicates complexity level. `product:` is always needed.
`aliases:`, `structure:` are almost always needed. `directories:`, `groups:`,
`environment:`, `releaseFlags:` appear only in configs that need them. Future sections
follow the same pattern.

## Phase 1 -- System Integration

Direct WixSharp support exists for all of these; they are self-contained additions with no new schema complexity.

### Registry Values

Write registry keys and values as part of the installation.

```yaml
registry:
  - root: HKLM
    key: "Software\\MyCompany\\MyApp"
    name: "Version"
    value: "1.0.0.0"
    type: string    # string, dword, qword, binary

  - root: HKCU
    key: "Software\\MyCompany\\MyApp"
    name: "InstallPath"
    value: "[INSTALLDIR]"
    type: string
```

Supported roots: `HKLM`, `HKCU`, `HKCR`, `HKU` (aliases for HKEY_LOCAL_MACHINE, etc.)

### Environment Variables

Set machine or user environment variables during installation.

```yaml
environment:
  - name: "MYAPP_HOME"
    value: "[INSTALLDIR]"
    scope: machine    # machine or user

  - name: "MYAPP_MODE"
    value: "production"
    scope: user
```

### Empty Directory Creation

Create directories in the install tree that start empty. Useful for drop-in locations (tools, plugins, user data) that the application or user populates after install.

```yaml
structure:
  - folder: "tools\\vsproj"    # created empty; user drops vsproj.exe here
  - folder: "plugins\\custom"  # extension point for third-party plugins
```

---

## Phase 2 -- Installer Features

Add support for optional installer components that users can select during installation. This maps directly to WiX `Feature` elements.

```yaml
features:
  - id: Core
    title: "Core Application"
    default: true
    visible: false      # always installed, not shown in UI

  - id: Plugins
    title: "Plugin Pack"
    default: false
    visible: true

  - id: Documentation
    title: "Documentation"
    default: true
    visible: true
```

Files, shortcuts, registry entries, and environment variables will gain an optional `feature` field to associate them with a specific feature. Unassigned items belong to the default feature.

---

## Phase 3 -- File Groups and Extended Sources

### File Groups

Introduce named file groups as an alternative to inline `structure` entries. Groups provide a cleaner way to organize files, especially when the same set of files needs to be referenced in multiple places, associated with a feature, or installed outside `INSTALLDIR`.

```yaml
groups:
  - id: CoreFiles
    feature: Core
    destinationFolder: "[INSTALLDIR]\\App"
    sourceFolder: "src/MyApp/bin/Release"
    scan:
      include: ["*.dll", "*.exe"]
      exclude: ["*.pdb", "*.xml"]

  - id: PluginFiles
    feature: Plugins
    destinationFolder: "[INSTALLDIR]\\Plugins"
    files:
      - source: "plugins/pluginA.dll"
        rename: "plugin.dll"
      - source: "plugins/readme.txt"
```

Individual files gain support for `rename` and explicit component `guid` assignment.

The `destinationFolder` can reference any named directory ID from the `directories` block (see Phase 4), allowing files to be installed outside `INSTALLDIR` -- for example, into `%APPDATA%` or the PowerShell module directory.

### Named Directories

Define named directory IDs for locations outside `INSTALLDIR`. Referenced in `groups` and `shortcuts` as install destinations.

```yaml
directories:
  - id: CONFIGDIR
    path: "[AppDataFolder]\\MyCompany\\MyApp"

  - id: PSMODDIR
    path: "[PersonalFolder]\\WindowsPowerShell\\Modules\\MyApp"
```

Standard WiX folder properties (`[AppDataFolder]`, `[PersonalFolder]`, `[CommonAppDataFolder]`, `[LocalAppDataFolder]`, etc.) are supported as path roots.

### Recursive Source

Copy an entire directory tree (not just a flat glob). Needed for content like mdBook help output, pre-built web assets, or any folder with subdirectories.

```yaml
structure:
  - folder: "help"
    contents:
      - source: "help:**/*"    # ** = recurse into subdirectories
```

The `**` glob syntax recursively enumerates all files, preserving the relative subdirectory structure under the destination folder.

### Conditional File Install

Install a file only if it does not already exist at the destination. Useful for default config files that should not be overwritten on upgrade.

```yaml
groups:
  - id: DefaultConfig
    destinationFolder: "[AppDataFolder]\\MyCompany"
    condition: notExists
    files:
      - source: "installer/myapp.config.ini"
```

---

## Phase 4 -- Release Flags and Conditional Logic

The most significant feature for complex/multi-client installers. Release flags are boolean switches passed at build time that control which content is included in the MSI. A single config file can produce different installers for different customers, deployment targets, or installation scopes.

```yaml
releaseFlags:
  - ClientA
  - ClientB
  - InternalTools

defaultActiveFlags:
  - ClientA
```

The `enabledIf` condition can be applied to features, file groups, individual files, shortcuts, registry entries, and environment variables:

```yaml
features:
  - id: Reports
    title: "Client Reports"
    enabledIf: ClientA

groups:
  - id: ClientAReports
    feature: Reports
    enabledIf: ClientA
    destinationFolder: "[INSTALLDIR]\\Reports"
    sourceFolder: "reports/client_a"
    scan:
      include: ["*.rpt"]

shortcuts:
  - name: "Reports"
    target: "[INSTALLDIR]\\Reports\\index.rpt"
    folder: "[ProgramMenuFolder]"
    enabledIf: ClientA
```

Flags are activated via a new `--flag` CLI option:

```
AlliePack.exe allie-pack.yaml --flag ClientA --flag InternalTools
```

### Scope-Variant Installation Targets

Release flags extend naturally to installation scope, allowing a single config to produce both a per-user and a per-machine MSI. Any field that accepts a plain value can instead accept a conditional map keyed by flag name, with an optional `_else` fallback.

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

  installDir:
    PerUser:    "[LocalAppDataFolder]\\Programs\\MyCompany\\MyApp"
    PerMachine: "[ProgramFiles]\\MyCompany\\MyApp"
    _else:      "[LocalAppDataFolder]\\Programs\\MyCompany\\MyApp"

directories:
  - id: CONFIGDIR
    path:
      PerUser:    "[AppDataFolder]\\MyCompany"
      PerMachine: "[CommonAppDataFolder]\\MyCompany"
      _else:      "[AppDataFolder]\\MyCompany"

  - id: PSMODDIR51
    path:
      PerUser:    "[PersonalFolder]\\WindowsPowerShell\\Modules\\MyApp"
      PerMachine: "[ProgramFiles]\\WindowsPowerShell\\Modules\\MyApp"
      _else:      "[PersonalFolder]\\WindowsPowerShell\\Modules\\MyApp"

environment:
  - name: MYAPP_HOME
    value: "[INSTALLDIR]"
    scope:
      PerUser:    user
      PerMachine: machine
      _else:      user
```

Build both targets from the same config:

```
AlliePack.exe allie-pack.yaml --flag PerUser    -D VERSION=1.2.0 --output MyApp-user.msi
AlliePack.exe allie-pack.yaml --flag PerMachine -D VERSION=1.2.0 --output MyApp-machine.msi
```

The conditional map syntax (`FlagName: value`, `_else: fallback`) is consistent across all fields that support it: `product.installScope`, `product.installDir`, `directories[].path`, `environment[].scope`, `environment[].value`, `registry[].value`, and variable definitions.

---

## Phase 5 -- Variables and Includes

### Variable Substitution

Define named variables in the config and reference them with `{varName}` syntax throughout the file. Complements `--define` (which substitutes `[KEY]` tokens before parsing); variables are resolved after parsing and support conditional values.

```yaml
variables:
  version: "1.0.0.0"
  company: "My Company"

  # Conditional variable -- value depends on active release flag
  productName:
    ClientA: "Customer A Suite"
    ClientB: "Customer B Suite"
    _else: "MyApp"

product:
  name: "{productName}"
  manufacturer: "{company}"
  version: "{version}"
```

### Modular Includes

Split large configurations into reusable files and compose them with `includes`.

```yaml
includes:
  - shared/common-shortcuts.yaml
  - components/plugin-feature.yaml

product:
  name: "My App"
  ...
```

Included files follow the same schema and are merged before processing.

---

## Phase 6 -- Runtime Scope Selection (Single-MSI Install-for-Me / Install-for-All)

The Phase 4 approach produces two separate MSIs from one config — a deliberate
design choice that keeps each installer simple and predictable. This phase adds
an alternative: a **single MSI** that presents a scope choice in the installer UI
and adapts its install paths at runtime.

### Why two MSIs is usually the right call

The two-MSI model is the standard for developer and enterprise tools:
- IT administrators deploy the per-machine MSI silently via SCCM/Intune; end users
  self-install the per-user MSI without elevation.
- Each MSI is independently signed, tested, and distributed through separate channels.
- The user never sees a confusing "install for me vs everyone" choice.

### When single-MSI scope selection is worth it

Consumer software that ships one download link for everyone (VS Code, Notepad++, 7-Zip)
benefits from a single MSI. The user sees a radio button in the installer UI:
- **Install for me only** -- no elevation, per-user paths
- **Install for all users** -- requires admin elevation, per-machine paths

### How it works under the hood

The MSI engine uses the `ALLUSERS` property to switch scope at runtime:
- `ALLUSERS=1` → per-machine, elevation required
- `ALLUSERS=""` → per-user, no elevation

WiX provides the `WixUI_Advanced` dialog set which includes this screen. Install
paths use `[APPLICATIONFOLDER]`, a WiX property that resolves differently based on
`ALLUSERS`. All conditional paths must be WiX runtime properties — they cannot be
resolved by AlliePack at build time.

### Proposed AlliePack syntax

```yaml
product:
  installMode: choice          # single MSI with runtime scope dialog
  # installMode: perUser       # (default) build a dedicated per-user MSI
  # installMode: perMachine    # build a dedicated per-machine MSI

  # installDir is split into two runtime-resolved properties:
  installDirUser:    "[LocalAppDataFolder]\\Programs\\MyCompany\\MyApp"
  installDirMachine: "[ProgramFiles]\\MyCompany\\MyApp"
```

`installMode: choice` changes the installer to use `WixUI_Advanced` and renders the
scope-selection dialog. All other config fields (directories, env vars, etc.) remain
the same; AlliePack emits them with appropriate WiX conditions automatically.

---

## Phase 7 -- Winget Package Manifest Generation

[Windows Package Manager (winget)](https://learn.microsoft.com/en-us/windows/package-manager/)
is the official Windows package manager, backed by the community
[winget-pkgs](https://github.com/microsoft/winget-pkgs) repository. Publishing a
winget manifest makes your tool installable with a single command:

```
winget install MyCompany.MyApp
```

AlliePack already knows most of what a winget manifest needs — product name, version,
manufacturer, platform, install scope, and upgrade code. This phase adds manifest
generation as a build output alongside the MSI.

### What winget manifests contain

A winget submission is a folder of YAML files:
- **version manifest** -- package identity and version
- **installer manifest** -- MSI download URL, SHA256 hash, scope, silent install flags
- **defaultLocale manifest** -- description, license, publisher URL, tags

### Proposed config additions

```yaml
product:
  name: "MyApp"
  manufacturer: "MyCompany"
  version: "1.2.3.0"
  ...

winget:
  packageIdentifier: "MyCompany.MyApp"   # winget store ID (required)
  publisherUrl: "https://mycompany.com"
  licenseUrl: "https://mycompany.com/license"
  tags:
    - developer-tools
    - cli
  shortDescription: "A short one-line description for the winget catalog"
  # installerUrl and sha256 are set at publish time (post-build)
```

### Proposed CLI usage

```
# Build MSI and generate winget manifests alongside it
AlliePack.exe allie-pack.yaml --flag PerUser -D VERSION=1.2.3 --output dist\MyApp-user.msi --winget dist\winget

# Output in dist\winget\:
#   MyCompany.MyApp.yaml           (version manifest)
#   MyCompany.MyApp.installer.yaml (installer manifest, URL placeholder)
#   MyCompany.MyApp.locale.en-US.yaml
```

AlliePack fills in all metadata it knows at build time. The installer URL and SHA256
are injected in a post-build step (or left as placeholders for a CI pipeline to fill
before submitting the PR to winget-pkgs).

This feature makes AlliePack a complete distribution pipeline for developer tools:
one config drives both the MSI build and the winget submission package.

---

## Phase 8 -- Bundles (Bootstrap EXE + Prerequisites)

> **Complexity warning.** WiX Burn bundles are the most powerful — and most
> complicated — thing in the WiX ecosystem. This phase is deliberately last
> because it introduces a new output type (a signed `.exe` bootstrapper rather
> than an `.msi`), new concepts (chaining, elevation strategy, detection
> conditions), and new infrastructure (code signing). Everything in Phases 1-7
> works without touching any of this.

### What a bundle is

A Burn bootstrapper is a self-extracting `.exe` that chains one or more
packages together in sequence: install .NET if missing, install VC++ redist,
install the main MSI, etc. It handles UAC elevation once at launch rather than
mid-install, shows a unified progress UI, and rolls back all packages on failure.

### Three tiers of bundle support

AlliePack will approach bundles in increasing order of magic:

**Tier 1 -- Multi-build config**
A single `.allie.yaml` declares multiple MSI outputs. No bootstrapper, no
chaining -- just a convenience for configs that currently require two
`--flag` invocations. The output is still separate MSIs.

```yaml
builds:
  - name: perUser
    flags: [PerUser]
    output: "dist/MyApp-user.msi"
  - name: perMachine
    flags: [PerMachine]
    output: "dist/MyApp-machine.msi"
```

**Tier 2 -- Chained AlliePack configs**
A `bundle:` top-level block wraps multiple AlliePack configs (or a mix of
configs and external packages) into a Burn bootstrapper. AlliePack builds
each referenced config as an MSI, then chains them.

```yaml
bundle:
  output: "dist/MyApp-setup.exe"
  chain:
    - config: "prereqs/runtime.allie.yaml"   # built by AlliePack
    - config: "allie-pack.yaml"              # the main product
```

**Tier 3 -- Named prerequisites (magic)**
AlliePack knows about well-known prerequisites by name -- their download URLs,
detection conditions, and silent install flags. Declare a prereq by name and
AlliePack handles the rest.

```yaml
bundle:
  output: "dist/MyApp-setup.exe"
  prerequisites:
    - package: "dotnet481"       # .NET Framework 4.8.1
    - package: "vcredist2022-x64"
  chain:
    - config: "allie-pack.yaml"
```

Named prerequisites that AlliePack will understand out of the box:
`dotnet481`, `dotnet6`, `dotnet8`, `vcredist2022-x64`, `vcredist2022-x86`.

### Elevation and UAC

The bootstrapper requests elevation once at launch if any chained package
requires it (i.e., any per-machine MSI in the chain). Per-user-only chains
run without elevation. AlliePack infers the required elevation level from the
chain and sets the bootstrapper manifest accordingly -- no explicit config
needed.

### Code signing

Burn bootstrapper EXEs must be signed to avoid SmartScreen warnings. MSIs
benefit from signing too. This introduces a `signing:` block that applies to
all outputs from the config:

```yaml
signing:
  thumbprint: "ABCDEF1234..."        # certificate thumbprint in local cert store
  # or
  pfx: "certs/MyApp.pfx"
  pfxPassword: "[SIGN_PASSWORD]"     # injected via --define at build time
  timestampUrl: "http://timestamp.digicert.com"
```

`signing:` is a top-level block (not under `product:`) so it remains invisible
to configs that don't need it. When present it applies to all MSI outputs and,
if a bundle is declared, to the bootstrapper EXE as well.

---

## Deferred / Out of Scope

The following are explicitly not planned for the near term:

- **Bootstrap EXE** -- promoted to Phase 8 above
- **COM+ / DCOM registration** -- niche requirement
- **ODBC / database DSN setup** -- niche requirement
- **Font installation** -- low demand
- **Custom UI beyond managed dialogs** -- WixSharp managed UI covers the common cases

---

## Schema Validation

At some point it will be useful to add YAML schema validation so that config errors are caught with clear messages before the build starts. The [AlliePack-docs](https://github.com/seraphire/AlliePack-docs) repo contains a JSON Schema and NJsonSchema-based validator that can be ported once the schema stabilizes.
