# Schema Guide

This guide explains how AlliePack config works as a system — the concepts, the relationships between sections, and the reasoning behind the design. For a field-by-field lookup table, see the [Schema Reference](schema-reference.md).

---

## How files get into your MSI

Everything installed by an MSI lives in a directory tree rooted at `INSTALLDIR` — the folder the user picks during installation (or the default for your `installScope`). AlliePack builds this tree from the `structure:` section of your config.

### The `structure:` tree

`structure:` is a list of elements. Each element either contributes files at the current level or creates a subdirectory with its own contents. Elements can be nested to any depth.

```yaml
structure:
  - source: "bin:MyApp.exe"          # file at root of INSTALLDIR
  - source: "bin:*.dll"              # all DLLs at root of INSTALLDIR

  - folder: "Data"
    contents:
      - source: "assets:*.json"      # JSON files under INSTALLDIR\Data

  - folder: "Plugins"
    contents:
      - folder: "Core"
        contents:
          - source: "plugins:*.dll"  # under INSTALLDIR\Plugins\Core
```

The `folder:` key creates the subdirectory. Files inside it go in `contents:`. Omitting `folder:` puts files at the current level.

### Three ways to specify files

**1. Glob pattern (`source:`)** — direct file paths and globs:

```yaml
- source: "bin:MyApp.exe"            # single file
- source: "bin:*.dll"                # all DLLs in the bin directory
- source: "bin:**/*.dll"             # all DLLs recursively, preserving subdirectory structure
- source: "[GitRoot]/data/config.json"  # token-based path
```

**2. Visual Studio project (`project:`)** — AlliePack reads the `.csproj` or `.vbproj` file, figures out where the build output goes for the given `configuration` and `platform`, and includes all files from that directory. You don't need to know the output path or list files manually.

```yaml
- project: "src/MyApp/MyApp.csproj"
  configuration: Release
  platform: x64
  excludeFiles:
    - "*.pdb"
    - "*.xml"
```

**3. Visual Studio solution (`solution:`)** — AlliePack reads the `.sln` file and does the same for every project in the solution. Use `excludeProjects` to filter out test projects or other outputs you don't want packaged.

```yaml
- solution: "MyApp.sln"
  configuration: Release
  excludeProjects:
    - "MyApp.Tests"
    - "MyApp.Benchmarks"
  excludeFiles:
    - "*.pdb"
```

### Mixing sources

You can mix all three in the same `structure:` block or the same `folder:`. Each element contributes independently, and AlliePack de-duplicates files that appear more than once (by source path).

```yaml
structure:
  - solution: "MyApp.sln"             # all project outputs
    configuration: Release
    excludeProjects: ["MyApp.Tests"]
    excludeFiles: ["*.pdb"]

  - folder: "Themes"
    contents:
      - source: "assets:themes/**/*"  # additional assets not in the solution output
```

### The `**` recursive glob

`**` in a source pattern means "recurse into all subdirectories". The matched files are installed preserving their relative paths under the destination folder:

```yaml
- folder: "Help"
  contents:
    - source: "help-output:**/*"
```

If `help-output` contains `images/logo.png` and `pages/intro.html`, AlliePack installs them at `INSTALLDIR\Help\images\logo.png` and `INSTALLDIR\Help\pages\intro.html`.

Without `**`, only files directly in the alias directory are matched.

---

## Path resolution

AlliePack resolves paths in a specific order. Understanding this makes it much easier to debug why a file isn't being found.

### Built-in tokens

These are replaced first, before any other resolution:

| Token | Value |
|---|---|
| `[YamlDir]` | The directory containing your `allie-pack.yaml` file |
| `[GitRoot]` | The root of the nearest git repository (walked up from `YamlDir`) |
| `[CurrentDir]` | The directory from which you ran `AlliePack.exe` |

These three are always available. `[GitRoot]` falls back to `[YamlDir]` if there's no git repository.

### User-defined paths and defines

Values in `paths:` define custom tokens available as `[name]`. Command-line `--define KEY=VALUE` overrides any `paths.KEY` with the same name.

```yaml
paths:
  srcRoot: "[CurrentDir]"       # default: wherever you run AlliePack from
  version: "1.0.0.0"
```

```
# Override from the command line without changing the YAML:
AlliePack.exe allie-pack.yaml --define srcRoot=D:\ci\src
```

This is the key pattern for CI/CD: your YAML file works correctly locally (using `[CurrentDir]`), and in CI you pass the actual source path as a define.

### Aliases

Aliases are short names for directories, used in `source:` fields with `alias:pattern` syntax. They are just a convenience over repeating a long path — internally they resolve to the same token/path mechanism.

```yaml
aliases:
  bin: "[GitRoot]/src/MyApp/bin/Release/net481"
  assets: "[YamlDir]/installer/assets"
```

```yaml
structure:
  - source: "bin:*.dll"          # expands to [GitRoot]/src/MyApp/bin/Release/net481/*.dll
  - source: "assets:logo.png"    # expands to [YamlDir]/installer/assets/logo.png
```

### Relative paths

If a `source:` value doesn't use an alias prefix or a token, it's treated as relative to the YAML file's directory:

```yaml
structure:
  - source: "resources/readme.txt"    # relative to the directory containing allie-pack.yaml
```

---

## Tokens vs. WiX properties

AlliePack has two distinct kinds of variable: **build-time tokens** (resolved when AlliePack runs) and **WiX install-time properties** (resolved when the MSI runs on the user's machine).

**Build-time tokens** use `[UpperCase]` brackets and include `[YamlDir]`, `[GitRoot]`, user-defined `[paths]`, and `--define` values. They are expanded before WiX ever sees the config.

**WiX properties** also use `[UpperCase]` brackets, but they aren't known until the MSI is running: `[INSTALLDIR]`, `[AppDataFolder]`, `[CommonAppDataFolder]`, etc. They appear in shortcut targets, registry values, environment variable values, and service executable paths — places that need to reference the actual installation location on the target machine.

The practical rule: if the field is a file path used to find files at build time (like `source:` or `aliases:`), use build-time tokens. If the field describes a path where something will be installed at runtime (like `shortcuts[].target` or `registry[].value`), use WiX properties.

---

## Install scope

`installScope` controls whether the installer targets the current user or all users on the machine.

| Value | Behavior |
|---|---|
| `perMachine` | Installs to `Program Files`, requires elevation, available to all users |
| `perUser` | Installs to the user's `AppData\Local\Programs`, no elevation required |
| `both` | Supports both; scope is selected at build time with `--scope` |

For `both`, AlliePack infers sensible defaults for paths that depend on scope. You don't need conditional maps for the common case:

```yaml
product:
  installScope: both
  # installDir defaults automatically:
  #   per-user:    %LocalAppData%\Programs\MyCompany\MyApp
  #   per-machine: C:\Program Files\MyCompany\MyApp
```

Build both targets:

```
AlliePack.exe allie-pack.yaml --scope perUser    --output dist\MyApp-user.msi
AlliePack.exe allie-pack.yaml --scope perMachine --output dist\MyApp-machine.msi
```

---

## Release flags and conditional maps

Release flags let you produce different MSI outputs from a single config file. A flag is activated at build time with `--flag <name>`, and any field that supports a conditional map resolves to the value for that flag.

```yaml
releaseFlags:
  - ClientA
  - ClientB

defaultActiveFlags:
  - ClientA    # used when no --flag is passed
```

Any field that accepts a plain value also accepts a conditional map:

```yaml
product:
  name:
    ClientA: "Customer A Suite"
    ClientB: "Customer B Suite"
    _else:   "MyApp"            # fallback when no matching flag is active

  installDir:
    ClientA: "[ProgramFiles64Folder]\\CustomerA\\Suite"
    ClientB: "[ProgramFiles64Folder]\\CustomerB\\Suite"
    _else:   "[ProgramFiles64Folder]\\MyApp"
```

Flags compose naturally with `--define` for version injection:

```
AlliePack.exe allie-pack.yaml --flag ClientA -D VERSION=2.1.0 --output dist\ClientA-2.1.0.msi
AlliePack.exe allie-pack.yaml --flag ClientB -D VERSION=2.1.0 --output dist\ClientB-2.1.0.msi
```

---

## Files outside `INSTALLDIR`: directories and groups

Not everything belongs in the main application folder. Config files might go in `AppData`, PowerShell modules in the PS modules directory, and so on. This is what `directories:` and `groups:` are for.

`directories:` defines named destinations by either a full path or a well-known `type:`:

```yaml
directories:
  - id: CONFIGDIR
    type: config          # AppData (per-user) or ProgramData (per-machine)
    subPath: "MyCompany\\MyApp"

  - id: PSMODDIR
    type: psmodules51
    subPath: "MyApp"
```

`groups:` then assigns files to those destinations:

```yaml
groups:
  - id: DefaultConfig
    destinationDir: CONFIGDIR
    condition: notExists           # only install if file doesn't already exist
    files:
      - source: "installer/myapp-defaults.config"
        rename: "config.ini"

  - id: PowerShellModule
    destinationDir: PSMODDIR
    files:
      - source: "scripts:MyApp.psm1"
      - source: "scripts:MyApp.psd1"
```

`condition: notExists` is especially useful for default config files that should be installed on a fresh install but left alone on upgrade — the user may have customized the file.

`permanent: true` on a group means the files are left on disk when the product is uninstalled. Use this for user data or generated files that the application writes after installation.

---

## Optional installer features

`features:` lets you present a tree of selectable components in the installer UI. Users can choose which features to install. Each feature has its own `structure:`, `shortcuts:`, `services:`, and other blocks.

```yaml
features:
  - id: MainApp
    name: "Application"
    default: true
    structure:
      - source: "bin:MyApp.exe"

  - id: PowerShellTools
    name: "PowerShell Integration"
    description: "Installs cmdlets for scripting MyApp"
    default: false
    groups:
      - id: PSModule
        destinationDir: PSMODDIR
        files:
          - source: "scripts:MyApp.psm1"
```

Content in the top-level `structure:`, `shortcuts:`, etc. blocks is always installed regardless of feature selection. Features contain the optional, selectable content.

---

## Windows services

Services declared in `services:` are installed and started during the MSI install, and stopped and removed during uninstall. AlliePack handles the WiX service install/control actions automatically.

The `executable:` field uses WiX install-time properties because the path is only known at install time:

```yaml
services:
  - name: "MyAppService"
    displayName: "My App Service"
    executable: "[INSTALLDIR]\\MyAppService.exe"
    start: auto
    account: LocalSystem
    onFailure:
      first: restart
      second: restart
      third: none
      restartDelaySeconds: 30
      resetAfterDays: 1
```

For services that depend on each other (or on third-party services), use `dependsOn:`:

```yaml
- name: "MyWorker"
  executable: "[INSTALLDIR]\\MyWorker.exe"
  dependsOn:
    - "MyAppService"     # service name
    - "MSSQLSERVER"      # external dependency
```

---

## Signing order

When `signing.files:` is configured, AlliePack signs packaged files **before** WiX packages them into the MSI. This is the industry-standard approach — the MSI then contains already-signed binaries, and the MSI itself is signed afterward.

```
sign files in-place  ->  build MSI  ->  sign MSI
```

See [Signing](signing.md) for the full signing reference.

---

## WiX fragments: the escape hatch

For anything AlliePack doesn't natively support, you can inject raw WiX XML directly. Fragments are merged into the generated `.wxs` document before it goes to the WiX compiler.

```yaml
wix:
  fragments:
    - inline: |
        <Fragment>
          <Property Id="MYPROPERTY" Value="1" />
        </Fragment>
    - file: "installer/custom-registration.wxs"
```

Use `--keep-wxs` to see the full merged document that WiX receives, which makes it easier to write and debug fragments.
