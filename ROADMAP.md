# AlliePack Roadmap

This document tracks planned features and their implementation priority. Items are grouped into phases based on dependency order and complexity. The [AlliePack-docs](https://github.com/seraphire/AlliePack-docs) repository contains detailed design specifications and YAML schema examples for many of these features.

## Phase 1 -- System Integration

Direct WixSharp support exists for both of these; they are self-contained additions with no new schema complexity.

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

## Phase 3 -- File Groups

Introduce named file groups as an alternative to inline `structure` entries. Groups provide a cleaner way to organize files, especially when the same set of files needs to be referenced in multiple places or associated with a feature.

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

---

## Phase 4 -- Release Flags and Conditional Logic

The most significant feature for complex/multi-client installers. Release flags are boolean switches passed at build time that control which content is included in the MSI. This allows a single config file to produce different installers for different customers or deployment targets.

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

## Deferred / Out of Scope

The following are explicitly not planned for the near term:

- **Bootstrap EXE** -- bundled installer that installs prerequisites; significant complexity
- **COM+ / DCOM registration** -- niche requirement
- **ODBC / database DSN setup** -- niche requirement
- **Font installation** -- low demand
- **Custom UI beyond managed dialogs** -- WixSharp managed UI covers the common cases

---

## Schema Validation

At some point it will be useful to add YAML schema validation so that config errors are caught with clear messages before the build starts. The [AlliePack-docs](https://github.com/seraphire/AlliePack-docs) repo contains a JSON Schema and NJsonSchema-based validator that can be ported once the schema stabilizes.
