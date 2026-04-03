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
- An average developer should never be forced to read past what they need; an installer
  engineer who knows WiX should be able to find and use every capability

**Manage complexity -- don't hide it.** There is a critical difference. Hiding
complexity means an expert hits a wall with no way through. Managing it means the
expert never has to deal with it *unless they choose to* -- but when they do, the
full power is there. Every abstraction should have a reach-through for someone who
knows what they are doing.

**The traceability test:** an installer engineer looking at AlliePack's generated WiX
output should be able to recognise what each part does and trace it back to the YAML
that produced it. If the abstraction is so thick that the generated XML is
unrecognisable, the feature has been over-engineered and will eventually trap someone.

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

## Phase 6 -- Project Type Resolvers

AlliePack already has two project type resolvers: `solution:` and `project:` know
how to find Visual Studio build outputs without the user listing files manually.
This phase generalises that pattern into a first-class, extensible system for other
project ecosystems.

### The concept

A **project type resolver** answers the question: *given a project root, what files
should be installed, where should they go, and what additional installer steps are
needed?* The `solution:` resolver already does this for MSBuild. The goal is the
same capability for Electron, ASP.NET published sites, Node packages, and others —
and a mechanism for users to define their own.

The syntax is consistent with what already exists: a new keyword in `structure:`
alongside `solution:` and `project:`.

### Built-in resolvers

**Electron**

Electron apps produce a platform-specific folder (`dist/`, `out/`) containing the
main EXE, Chromium runtime, `resources/`, `locales/`, and supporting DLLs. The
whole tree is installed recursively. The resolver knows what to exclude (`.map`
files, crash reporter symbols) and what the executable name is.

```yaml
structure:
  - type: electron
    path: "."              # project root (package.json found here)
    # defaults: reads "name" and "version" from package.json if not set in product:
    # output: "dist"       # override if build tool uses a different output folder
    excludeFiles:
      - "*.map"
```

**ASP.NET / dotnet publish**

Runs (or finds) a `dotnet publish` output and installs the publish tree. Knows to
include `wwwroot/`, `web.config`, and all DLLs while excluding build intermediates.
Optionally configures an IIS virtual directory (when combined with a future
`iis:` block).

```yaml
structure:
  - type: aspnet
    project: "src/MyApp.Web/MyApp.Web.csproj"
    configuration: Release
    # publish output is resolved automatically; no manual path needed
```

**Node / npm package**

Installs the bundled output of an npm project. Expects either a pre-built `dist/`
folder or runs the build script. Reads `package.json` for name and version.

```yaml
structure:
  - type: npm
    path: "."
    script: "build"        # npm script to run before packaging (optional)
    output: "dist"
```

**COM+ Application**

COM+ projects require file installation plus component registration in the COM+
catalog. This resolver handles both: installs the DLLs and registers the COM+
application. Requires `regsvr32`-style registration or a `*.tlb` type library.
See also: Phase 10 (Custom Actions) for complex COM+ scenarios.

```yaml
structure:
  - type: com-plus
    project: "src/MyApp.ComServer/MyApp.ComServer.csproj"
    configuration: Release
    applicationName: "MyApp COM+ Application"
    # generates the correct registration actions automatically
```

### User-defined resolvers

Not every project type warrants a built-in resolver. Users can define their own
as a YAML template and share them via the `includes:` mechanism (Phase 5).

```yaml
# my-resolver.allie-type.yaml
resolver:
  id: my-electron-variant
  description: "Custom Electron layout for this org"
  outputDir: "release/win-unpacked"
  include:
    - "**/*"
  exclude:
    - "*.map"
    - "*.pdb"
    - "chrome_debug.log"
  installSubdir: "App"
```

```yaml
# allie-pack.yaml
includes:
  - my-resolver.allie-type.yaml

structure:
  - type: my-electron-variant
    path: "."
```

Community-maintained resolver libraries (published as YAML files or NuGet packages)
can provide types for frameworks AlliePack doesn't know about yet. The same
traceability principle applies: a resolver must produce the same WiX output as if
the user had written the `structure:` entries by hand — nothing opaque, nothing
that can't be inspected with `--report`.

### Installer theme packages

The same include/resolver mechanism supports **installer themes** — packages that
control the visual appearance and branding of the installer UI. A company that
ships multiple products can define a corporate theme once and apply it consistently
across all of them.

A theme package provides:
- Installer UI artwork (banner, dialog background, logo)
- Colour scheme and font overrides for the WPF managed UI
- Default dialog sequencing and wording (e.g. a standard license preamble)
- Optionally: a standard `shortcuts:` or `environment:` block shared across products

```yaml
# corp-theme.allie-theme.yaml  (maintained by the org, shared across products)
theme:
  banner:     "assets/installer-banner.bmp"
  background: "assets/installer-bg.bmp"
  primaryColor: "#1A3A5C"
  productFamily: "Acme Developer Tools"
  defaultLicense: "assets/standard-eula.rtf"
```

```yaml
# myapp/allie-pack.yaml
includes:
  - ../shared/corp-theme.allie-theme.yaml

product:
  name: "MyApp"
  ...
# UI picks up banner, background, and license from the theme automatically.
# Any field in the theme can be overridden locally.
```

Theme packages are a natural complement to the project type resolver system:
resolvers handle *what gets installed*, themes handle *how the installation looks*.
Both are expressed as includable YAML and follow the same zero-config-default rule —
a config without a theme include looks and works exactly as it does today.

---

## Phase 7 -- Runtime Scope Selection (Single-MSI Install-for-Me / Install-for-All)

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

## Phase 8 -- Winget Package Manifest Generation

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

## Phase 9 -- Bundles (Bootstrap EXE + Prerequisites)

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

### The trigger: `prerequisites:` is the bundle declaration

Adding a top-level `prerequisites:` block is all it takes. AlliePack sees it
and automatically produces a Burn bootstrapper `.exe` instead of a plain `.msi`.
No separate `bundle:` keyword, no WiX terminology to learn -- the presence of
prerequisites *is* the signal that a bundle is needed.

```yaml
# Without prerequisites: -- builds MyApp.msi
product:
  name: "MyApp"
  ...

# With prerequisites: -- builds MyApp-setup.exe (Burn bootstrapper)
prerequisites:
  - package: "dotnet481"
  - package: "vcredist2022-x64"
```

AlliePack will approach bundle support in increasing order of magic:

**Tier 1 -- Multi-build config**
A single `.allie.yaml` declares multiple MSI outputs. No bootstrapper, no
prerequisites -- just a convenience for configs that currently require two
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

**Tier 2 -- Named prerequisites (magic)**
AlliePack knows about well-known prerequisites by name -- their download URLs,
detection conditions, and silent install flags. Declare a prereq by name and
AlliePack handles the rest. The bootstrapper is generated automatically.

```yaml
prerequisites:
  - package: "dotnet481"           # .NET Framework 4.8.1
  - package: "vcredist2022-x64"    # Visual C++ 2022 x64 redistributable
```

Named prerequisites AlliePack will understand out of the box:
`dotnet481`, `dotnet6`, `dotnet8`, `vcredist2022-x64`, `vcredist2022-x86`.

**Tier 3 -- External or custom prerequisite packages**
Reference an external installer by URL or local path when a named package
isn't available, or chain another AlliePack config as a prerequisite.

```yaml
prerequisites:
  - package: "dotnet481"
  - url: "https://example.com/MyRuntime-setup.exe"
    sha256: "abc123..."
    silentArgs: "/quiet /norestart"
    detectCondition: "HKLM:\\Software\\MyRuntime\\Installed"
  - config: "prereqs/shared-components.allie.yaml"   # built by AlliePack first
```

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

## Phase 10 -- Action Sequencing

MSI installations run in defined **sequences** — ordered lists of actions that
the installer engine executes at specific stages. AlliePack currently places
everything in the default positions WixSharp chooses. This phase exposes
sequence control to the config author when the defaults aren't enough.

### The four sequences

| Sequence | When it runs | Context |
|---|---|---|
| `installUI` | Before install, in installer process | User, interactive |
| `installExecute` | During install, in installer service | System / elevated |
| `adminUI` | Before admin (`/a`) extract | User, interactive |
| `adminExecute` | During admin extract | System / elevated |

Most configs never need to touch these. The need arises when you have to do
something at a specific point relative to a standard action -- for example, stop
a service _before_ `InstallFiles`, or register a COM object _after_
`InstallFiles` but _before_ `InstallFinalize`.

### Execution contexts within a sequence

Actions within `installExecute` have additional context options:

| Context | Meaning |
|---|---|
| `immediate` | Runs in the UI process, user context; cannot make system changes |
| `deferred` | Runs inside the installer service transaction; elevated, can write files/registry |
| `rollback` | Runs if the installation fails, to undo deferred work |
| `commit` | Runs after the transaction is committed; no rollback possible |

Deferred actions cannot read MSI session properties directly. Data must be
passed through a `CustomActionData` property set by a preceding immediate action.
AlliePack will handle this wiring automatically when `execute: deferred` is set.

### Proposed syntax

```yaml
# Controlling when a built-in AlliePack action runs
environment:
  - name: "MYAPP_HOME"
    value: "[INSTALLDIR]"
    scope: user
    sequence: installExecute     # default; shown for illustration
    after: InstallFiles          # default positioning
```

Action ordering for most built-ins (env vars, registry, shortcuts) is handled
automatically. Explicit `sequence:` and `before:`/`after:` fields are an escape
hatch for unusual requirements.

---

## Phase 11 -- Custom Actions

> **This is the most advanced feature in the roadmap and the one most at odds with
> AlliePack's simplicity principle.** Custom actions require shipping compiled code
> (a .NET DLL, a script, or an EXE) alongside the installer. They are powerful and
> sometimes unavoidable, but they introduce complexity that no YAML abstraction fully
> hides. Approach with care.

### What custom actions are for

Custom actions run arbitrary code during installation:
- Stop/start a Windows service
- Generate a config file with machine-specific values
- Validate a license key
- Register with an external service
- Anything MSI standard actions can't do

### Types

| Type | Description |
|---|---|
| `managed` | C# method in a WixSharp CustomAction DLL (strongly typed, debuggable) |
| `exe` | Run an EXE with arguments; quiet or visible |
| `script` | Inline VBScript or JScript (legacy; avoid for new work) |

### Proposed syntax

```yaml
actions:
  # Stop a service before files are replaced
  - id: StopMyService
    type: exe
    path: "sc.exe"
    args: "stop MyService"
    execute: immediate
    sequence: installExecute
    before: InstallFiles
    condition: "Installed"       # only on upgrade

  # Run a managed action after files are in place
  - id: PostInstallSetup
    type: managed
    dll: "MyApp.Installer.dll"   # path resolved via aliases
    method: "MyApp.Installer.Actions.PostInstall"
    execute: deferred
    impersonate: false           # false = run as SYSTEM in installer service
    sequence: installExecute
    after: InstallFiles
    data:                        # passed as CustomActionData to the deferred action
      installDir: "[INSTALLDIR]"
      version: "[ProductVersion]"

  # Rollback companion for the deferred action above
  - id: PostInstallSetup_Rollback
    type: managed
    dll: "MyApp.Installer.dll"
    method: "MyApp.Installer.Actions.PostInstallRollback"
    execute: rollback
    sequence: installExecute
    before: PostInstallSetup
```

### Deferred actions and CustomActionData

Deferred actions run inside the installer service and cannot read session
properties. AlliePack automatically generates the immediate `SetProperty`
action that serializes the `data:` block into `CustomActionData` and schedules
it immediately before the deferred action. The managed method receives it via
`session.CustomActionData`.

### The tension with simplicity

Custom actions are a trapdoor out of the declarative model. Once you write one
you own the compiled DLL, its signing, its versioning, and its compatibility with
future WiX versions. Before reaching for a custom action, consider whether:
- An environment variable, registry key, or post-install script can do the job
- A pre/post-install EXE action (`type: exe`) is sufficient
- The work belongs in the application itself rather than the installer

---

## WiX XML Fragments (Always Available)

> **Implemented.** This is not a future phase — it is available now.

When no AlliePack feature covers a requirement, raw WiX XML can be injected
directly into the generated `.wxs` document via a top-level `wix:` block.
This is the final escape hatch: anything WiX supports, AlliePack can express.

```yaml
wix:
  fragments:
    # Inline XML -- added as a direct child of the <Wix> root element
    - inline: |
        <Fragment>
          <ComponentGroup Id="SpecialComponents">
            <Component Id="SpecialReg" Directory="INSTALLDIR">
              <RegistryKey Root="HKCU" Key="Software\MyCompany\MyApp">
                <RegistryValue Name="SpecialFlag" Value="1" Type="integer"
                               KeyPath="yes"/>
              </RegistryKey>
            </Component>
          </ComponentGroup>
        </Fragment>

    # External .wxs file -- path resolved via aliases and tokens
    - file: "installer/custom-com-registration.wxs"
```

Fragments are injected after AlliePack builds the complete WiX document but
before it is handed to the WiX compiler. `--verbose` reports each fragment as
it is injected. `--report` lists the fragments without building.

**When to reach for this:**
- A WiX feature that has no AlliePack equivalent yet
- Complex COM or DCOM registration that the Phase 6 COM+ resolver doesn't cover
- Unusual component conditions or install sequencing requirements
- Referencing an existing hand-authored `.wxs` fragment from a previous project

**The traceability guarantee holds here too:** because you are writing WiX XML
directly, the generated output is your input — there is nothing hidden between
what you wrote and what the compiler sees.

---

## Extended Installer Capabilities (Future Phases)

The following are real installer requirements that AlliePack should eventually
support with first-class YAML syntax. None of them are out of scope — they are
simply not yet prioritised. Until dedicated support lands, all of them are
reachable today via `wix: fragments:`.

### System components and registration

| Capability | Notes |
|---|---|
| **COM / DCOM registration** | `regsvr32`-style self-registration, type library (`.tlb`) registration, class/interface/progid entries in HKCR |
| **COM+ application** | Register components in the COM+ catalog; set application identity, activation, pooling |
| **Windows Service** | Install, configure, start, stop, and remove a Windows service; set account, startup type, dependencies |
| **Service accounts** | Create a dedicated local user account for running a service under a least-privilege identity; or look up an existing account by name or SID to assign without creating one |
| **Windows Event Log** | Find or create an event log and register a custom event source; needed by services that write structured entries to Event Viewer |
| **IIS site / virtual directory** | Create or modify IIS sites, app pools, virtual directories, and application settings |
| **ODBC data source** | Register a system or user DSN; set driver, server, and connection parameters |
| **Performance counters** | Register custom performance counter categories and instances |
| **Scheduled tasks** | Create Windows Task Scheduler entries with trigger, action, and account settings |
| **Windows Firewall rules** | Open inbound/outbound ports or application exceptions |
| **Font installation** | Install `.ttf` / `.otf` fonts to the Windows Fonts folder |
| **GAC assembly registration** | Install a signed .NET assembly into the Global Assembly Cache |

### Installer experience

| Capability | Notes |
|---|---|
| **Custom installer UI** | Dialogs beyond the standard WPF managed UI set; custom welcome screens, product configuration pages |
| **Splash screen / pre-UI** | Display a splash or EULA before the main UI loads |
| **Bootstrap prerequisites UI** | Per-prerequisite progress display in the Burn bootstrapper (Phase 9) |

### Notes

- **Windows Service + Service Accounts + Event Log** form a natural cluster --
  in practice you rarely install a service without also needing a dedicated account
  to run it under and an event source to write to. These three will likely land
  together as a `services:` block. WixSharp has direct support for the service
  installer; account creation and event log setup are custom action territory
  (Phase 11) or `wix: fragments:` in the interim.
- **IIS** support is closely tied to the ASP.NET project type resolver (Phase 6)
  and will likely arrive alongside it.
- **COM / COM+** support will likely arrive as part of Phase 6's COM+ resolver,
  covering the common registration patterns; edge cases will remain in `wix: fragments:`.
- Everything else follows demand — if a capability surfaces repeatedly in real
  configs it moves up the list.

---

## Migration Tools

Two complementary pathways for getting existing installers into AlliePack.
Neither requires AlliePack to perfectly reproduce every feature of the source
installer -- the goal is to produce a clean, correct AlliePack config for
everything it *can* represent, and leave clearly-marked comments for anything
that needs manual attention.

---

### Legacy Project Converter

Reads an existing installer project file and generates an `allie-pack.yaml`.
The converter is a separate subcommand:

```
AlliePack.exe convert myapp.wse --output allie-pack.yaml
AlliePack.exe convert myapp.iss --output allie-pack.yaml
AlliePack.exe convert myapp.nsi --output allie-pack.yaml
```

**Priority target: InstallMaster `.wse`**

InstallMaster (Indigo Rose Software) stores projects in `.wse` files. The format
is parseable XML and covers files, registry, shortcuts, environment variables,
and custom actions. A converter would extract all of these and map them to their
AlliePack equivalents. Anything that doesn't yet have an AlliePack equivalent
is emitted as a commented-out `wix: fragments:` block or a `# TODO:` note so
nothing is silently lost.

**Other formats worth supporting over time**

| Format | Tool |
|---|---|
| `.wse` | InstallMaster 8.x (Indigo Rose) -- highest priority |
| `.iss` | Inno Setup |
| `.nsi` | NSIS (Nullsoft Scriptable Install System) |
| `.ism` | InstallShield |
| `.wxs` | WiX XML -- convert hand-authored WiX back to AlliePack YAML |
| `.aip` | Advanced Installer |

**What conversion produces**

For each source project the converter generates:
- A best-effort `allie-pack.yaml` with all directly mappable content
- `# TODO: [reason]` comments for anything requiring manual review
- A conversion report listing what was mapped, what was approximated, and
  what was not supported (with the raw source value preserved)
- A `wix: fragments:` section containing raw WiX XML for anything the converter
  could translate to WiX but not yet to AlliePack YAML

The output is intended to be a working starting point, not a perfect one-shot
migration. A developer should be able to run it, read the TODOs, fill in the
gaps, and have a working AlliePack config in an hour rather than a day.

---

### Installer Watch (Snapshot Diff)

Takes a before-and-after snapshot of the system around an install and generates
an AlliePack config from the observed changes. Useful when you have a compiled
installer but no source project, or when you want to understand what a black-box
installer actually does.

```
# Step 1: snapshot the system before installing
AlliePack.exe watch start --snapshot before.json

# Step 2: run your existing installer (manually or via script)
myapp-setup.exe /silent

# Step 3: snapshot the system after, diff, and emit AlliePack config
AlliePack.exe watch finish --snapshot before.json --output allie-pack.yaml
```

**What the watch captures**

| Category | Details |
|---|---|
| Files | New and modified files under Program Files, AppData, and user-specified paths |
| Registry | New keys and values under HKLM and HKCU (scoped -- not a full hive dump) |
| Environment variables | User and machine variables added or modified |
| Services | New services and their configuration |
| Scheduled tasks | New tasks in Task Scheduler |
| Shortcuts | New `.lnk` files in Start Menu and Desktop |
| Event log sources | New sources registered in the Windows Event Log |

**Scope and filtering**

A raw install snapshot is extremely noisy -- Windows itself writes constantly.
The watch applies filters in layers:

*Layer 1 -- Process ancestry (always on)*
Changes are attributed to the process that made them. Only changes from the
installer process tree are included by default. `SearchIndexer.exe`,
`MsMpEng.exe` (Defender), `svchost.exe`, and other system workers are excluded
regardless of what they touch.

*Layer 2 -- Built-in path and registry exclusions (always on)*
AlliePack ships a `windows-baseline` filter that covers well-known noise
locations:

| Category | Examples |
|---|---|
| Prefetch | `C:\Windows\Prefetch\*.pf` |
| Temp files | `%TEMP%\**`, `%TMP%\**`, `C:\Windows\Temp\**` |
| Search index | `C:\ProgramData\Microsoft\Search\**` |
| Thumbnails / icon cache | `%LOCALAPPDATA%\Microsoft\Windows\Explorer\**` |
| WER / crash dumps | `%LOCALAPPDATA%\Microsoft\Windows\WER\**` |
| ETW / diagnostic traces | `C:\Windows\System32\LogFiles\**` |
| Registry hive logs | `*.LOG1`, `*.LOG2` transaction files |
| Perf counters | `HKLM\...\Perflib\**` |
| Recent docs / jump lists | `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs\**` |
| Font cache | `C:\Windows\ServiceProfiles\LocalService\AppData\Local\FontCache\**` |
| .NET NGEN / assembly cache | `C:\Windows\assembly\NativeImages_**` |

*Layer 3 -- Named filter profiles (opt-in)*
Additional profiles cover noise from software that may be running during the
install. Applied via `--filter <name>`:

| Profile | Covers |
|---|---|
| `antivirus` | Defender quarantine updates, scan result caches |
| `visual-studio` | VS telemetry, extension update markers, `.suo` files |
| `office` | Office MRU, telemetry, template caches |
| `browser` | Chrome/Edge/Firefox cache and update activity |
| `windows-update` | WU staging, CBS logs, SoftwareDistribution churn |

*Layer 4 -- Project rules file (per-project)*
A `allie-watch-filters.yaml` file in the project directory (or passed via
`--filters`) lets teams define their own exclusions. The format mirrors
`.gitignore` in spirit -- glob patterns for paths, key patterns for registry:

```yaml
# allie-watch-filters.yaml
exclude:
  paths:
    - "C:\\ProgramData\\MyOtherProduct\\**"   # unrelated product updating in background
    - "%APPDATA%\\MyApp\\logs\\**"            # app writes logs on first run

  registry:
    - "HKCU\\Software\\MyCompany\\Telemetry\\**"

  processes:
    - "MyBackgroundSync.exe"                  # known unrelated background process
```

*Layer 5 -- Community filter packs*
As AlliePack matures, community-maintained filter packs can be published and
referenced by name -- similar to `.gitignore` templates on GitHub. Teams that
regularly install in environments with specific noise profiles (e.g. a corporate
endpoint with a particular AV product) can publish and share a filter pack rather
than duplicating rules across projects.

```
AlliePack.exe watch start --filter windows-baseline --filter antivirus --filter acme-corp
```

The goal is that by the time a developer looks at the watch output, it contains
only things the installer actually intended to do.

**What the output looks like**

The generated `allie-pack.yaml` is annotated with the source of each entry:

```yaml
# Observed: C:\Program Files\MyApp\myapp.exe
structure:
  - folder: "App"
    contents:
      - source: "[ProgramFiles]\\MyApp\\myapp.exe"   # TODO: replace with alias

# Observed: HKCU\Software\MyCompany\MyApp\InstallPath = C:\Program Files\MyApp
registry:                                             # TODO: registry (Phase 1)
  - root: HKCU
    key: "Software\\MyCompany\\MyApp"
    name: "InstallPath"
    value: "[INSTALLDIR]"
    type: string
```

**Relationship to the project converter**

The two tools are complementary:
- **Converter** -- you have the source project; use it for a clean, structured migration
- **Watch** -- you have only the compiled installer; use it to reverse-engineer what it does

A common workflow is to run Watch first to understand the installer's full scope,
then use the Converter on the source project if you can locate it, and reconcile
the two outputs to catch anything the Converter missed.

---

## Schema Validation

At some point it will be useful to add YAML schema validation so that config errors are caught with clear messages before the build starts. The [AlliePack-docs](https://github.com/seraphire/AlliePack-docs) repo contains a JSON Schema and NJsonSchema-based validator that can be ported once the schema stabilizes.

---

## Horizon: Cross-Platform Packaging

> **Pipe dream -- no implementation timeline.** Captured here because the idea
> is worth preserving and the existing architecture points toward it further
> than it might seem.

### The insight

AlliePack's YAML describes *what to install* -- product metadata, files, locations,
shortcuts, environment variables. It deliberately avoids WiX-specific concepts at
the schema level; WiX is the current backend, not the contract. That means the same
config could, in principle, drive a different packaging backend for a different
platform -- and produce something that belongs on macOS or Linux just as naturally
as it produces an MSI today.

### What cross-platform packaging looks like

**macOS**
| Format | Use case |
|---|---|
| `.pkg` | System installer; closest equivalent to MSI |
| `.dmg` | Drag-to-Applications; most common for consumer and developer tools |
| Homebrew formula | The natural target for developer CLI tools -- `brew install mytool` |

**Linux**
| Format | Use case |
|---|---|
| `.deb` | Debian / Ubuntu |
| `.rpm` | Red Hat / Fedora / SUSE |
| AppImage | Universal, no install; runs anywhere |
| Snap / Flatpak | Sandboxed, self-contained |
| Homebrew formula | Linuxbrew; same formula as macOS for developer tools |

### Why the existing architecture already helps

**Release flags (Phase 4)** provide the conditional map syntax that would drive
platform-specific paths and settings without needing a separate config:

```yaml
releaseFlags:
  - Windows
  - macOS
  - Linux

product:
  installDir:
    Windows: "[ProgramFiles]\\MyCompany\\MyApp"
    macOS:   "/usr/local/bin"
    Linux:   "/usr/local/bin"
    _else:   "[ProgramFiles]\\MyCompany\\MyApp"
```

**Project type resolvers (Phase 6)** would detect the target platform and find
the right build output. For .NET projects, `dotnet publish -r osx-x64` or
`-r linux-x64` produces a self-contained binary tree that a macOS or Linux
resolver would know how to package. For Rust or C++ projects that already have
cross-compilation set up, the resolver just points at the right output directory.

**Winget manifest generation (Phase 8)** extends naturally to Homebrew formulas
and Linux package metadata -- the information is the same (name, version, download
URL, SHA256, description); only the output format differs.

### How a cross-platform build would look

```yaml
# allie-pack.yaml -- same config, three platform targets
product:
  name: "mytool"
  version: "[VERSION]"
  manufacturer: "MyCompany"

releaseFlags: [Windows, macOS, Linux]
defaultActiveFlags: [Windows]

aliases:
  bin:
    Windows: "src/MyTool/bin/x64/Release/net8.0/win-x64/publish"
    macOS:   "src/MyTool/bin/x64/Release/net8.0/osx-x64/publish"
    Linux:   "src/MyTool/bin/x64/Release/net8.0/linux-x64/publish"

structure:
  - source: "bin:mytool*"
```

```
# Build all three from CI:
AlliePack.exe allie-pack.yaml --flag Windows -D VERSION=1.2.0 --output dist/mytool-win.msi
AlliePack.exe allie-pack.yaml --flag macOS   -D VERSION=1.2.0 --output dist/mytool-mac.pkg
AlliePack.exe allie-pack.yaml --flag Linux   -D VERSION=1.2.0 --output dist/mytool-linux.deb
```

### What it would actually take

This is not a small undertaking. The WiX/WixSharp backend is deeply integrated
today. Cross-platform support would require:

1. A **packaging backend abstraction** -- an interface that `InstallerBuilder`
   currently fills implicitly, made explicit so that `WixBackend`, `PkgBackend`,
   `DebBackend` etc. can be swapped in based on `--flag` or target platform
2. **Platform-specific schema fields** -- some things (WiX `fragments:`, Windows
   services, COM registration) are Windows-only and need to be gracefully ignored
   or flagged when targeting other platforms
3. **Platform toolchain dependencies** -- `pkgbuild`/`productbuild` on macOS,
   `dpkg-deb`/`rpmbuild` on Linux; these need to be present or AlliePack needs
   to ship cross-platform build containers
4. **Platform-aware path handling** -- forward vs back slashes, `/Applications`,
   `$HOME`, `XDG_DATA_HOME` etc.

None of that is impossible. The architecture is pointing the right direction.
It just needs the phases before it to stabilise first.
