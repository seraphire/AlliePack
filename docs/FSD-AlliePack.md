# AlliePack — Functional Specification

**Status:** Active development
**Version:** 0.8.x
**Last updated:** 2026-04-07

---

## 1. Purpose

AlliePack is a YAML-driven MSI installer builder. Its goal is to let a developer
describe *what* an installer should do in a clean, readable config file and have
the tool handle all WiX/MSI mechanics automatically — while never blocking an
installer engineer who needs to reach through to the underlying WiX capabilities.

---

## 2. Design Principles

**Manage complexity, don't hide it.**
Every abstraction must have a reach-through for experts. An installer engineer
reading the generated WiX XML output should be able to trace every element back
to the YAML that produced it.

**Progressive complexity.**
A minimal working config is a dozen lines of YAML. Every feature beyond that is
invisible to configs that don't use it — no required fields, no forced migration,
no new concepts that impose themselves on unrelated configs.

**New top-level blocks over crowding `product:`.**
When a feature needs config, it gets its own top-level section (`directories:`,
`groups:`, `environment:`, `wix:`). The `product:` block stays small.

---

## 3. System Overview

```
allie-pack.yaml
      |
      v
  AlliePack.exe
      |
      +-- Program.cs          CLI entry point; parses args, loads YAML
      +-- ConfigModels.cs     YAML schema / deserialization
      +-- PathResolver.cs     Resolves aliases, tokens, globs (including **)
      +-- InstallerBuilder.cs Converts config to WixSharp object model
      +-- SolutionResolver.cs Resolves Visual Studio solution/project outputs
      |
      v
  WixSharp / WiX v4
      |
      v
  MyApp.msi
```

**Runtime:** .NET Framework 4.8.1
**Installer backend:** WixSharp_wix4 v2.12.3 / WiX Toolset v4
**YAML parser:** YamlDotNet v16
**CLI parser:** CommandLineParser v2.9

---

## 4. CLI Interface

```
AlliePack.exe [config] [options]
```

| Argument | Description |
|---|---|
| `config` | Path to an `allie-pack.yaml` file, or a directory containing one. Defaults to `allie-pack.yaml` in the current directory. |
| `-o`, `--output <path>` | Output path for the generated MSI. Defaults to `<ProductName>.msi` in the current directory. |
| `-r`, `--report` | Dry run: print a content report instead of building the MSI. |
| `-v`, `--verbose` | Enable verbose output. |
| `-D`, `--define KEY=VALUE` | Substitute `[KEY]` tokens in the YAML before parsing. Repeatable. |
| `--flag <name>` | Activate a release flag. Selects values from conditional maps. Repeatable. Falls back to `defaultActiveFlags` in config. |

---

## 5. Config File Schema

Default filename: `allie-pack.yaml`
Alternative: `<projectname>.allie.yaml`

### 5.1 Top-level structure

```yaml
product:        # required
aliases:        # optional
structure:      # optional (at least one source of files expected)
shortcuts:      # optional
environment:    # optional
directories:    # optional
groups:         # optional
releaseFlags:   # optional
defaultActiveFlags: # optional
wix:            # optional — raw WiX XML escape hatch
```

### 5.2 `product:`

| Field | Type | Required | Default | Notes |
|---|---|---|---|---|
| `name` | string | yes | — | Product display name |
| `manufacturer` | string | yes | — | Publisher name |
| `version` | string | yes | — | Four-part: `1.0.0.0` |
| `description` | string | no | `""` | Control Panel description |
| `upgradeCode` | GUID string | yes | — | Stable across versions |
| `installScope` | string or conditional | no | `perMachine` | `perUser` or `perMachine` |
| `installDir` | string or conditional | no | `[ProgramFilesFolder]\Manufacturer\Name` | Install root path |
| `platform` | string | no | `x86` | `x86`, `x64`, `arm64` |
| `licenseFile` | string | no | — | Path to RTF license file |

### 5.3 `aliases:`

A flat map of alias names to directory paths. Used as `aliasName:relative/path`
in `source:` fields.

```yaml
aliases:
  bin: "src/MyApp/bin/Release/net481"
  assets: "installer/assets"
```

### 5.4 `structure:`

A list of `StructureElement` entries defining the INSTALLDIR file tree.

| Field | Type | Notes |
|---|---|---|
| `folder` | string | Creates a subdirectory under the current path |
| `source` | string | Glob pattern; supports `alias:path` and `**` recursive glob |
| `solution` | string | Path to a `.sln` file; resolves build outputs |
| `project` | string | Path to a `.csproj`/`.vbproj` file |
| `configuration` | string | Build configuration. Default: `Release` |
| `platform` | string | Build platform. Default: `Any CPU` |
| `excludeFiles` | list | Glob patterns to exclude from matched files |
| `excludeProjects` | list | Project names to exclude from solution resolve |
| `contents` | list | Nested `StructureElement` entries |

**Glob tokens available in `source:`:**

| Token | Resolves to |
|---|---|
| `[YamlDir]` | Directory containing the YAML file |
| `[GitRoot]` | Root of the nearest git repository |
| `[CurrentDir]` | Working directory at invocation |
| `aliasName:` | Path defined in `aliases:` |
| `**` | Recursive directory descent; preserves relative path structure |

### 5.5 `shortcuts:`

| Field | Type | Notes |
|---|---|---|
| `name` | string | Shortcut display name |
| `target` | string | Path; supports `[INSTALLDIR]` and WiX folder properties |
| `description` | string | Tooltip text |
| `folder` | string | Destination; e.g. `[ProgramMenuFolder]`, `[DesktopFolder]` |

### 5.6 `environment:`

| Field | Type | Notes |
|---|---|---|
| `name` | string | Variable name |
| `value` | string or conditional | Value; supports `[INSTALLDIR]` and WiX properties |
| `scope` | string or conditional | `user` or `machine` |

### 5.7 `directories:` and `groups:`

Named install destinations outside INSTALLDIR.

**`directories:`** — defines named directory IDs:

| Field | Type | Notes |
|---|---|---|
| `id` | string | Reference name used in `groups:` |
| `path` | string or conditional | Destination path; supports WiX folder properties |

**`groups:`** — files installed to named directories:

| Field | Type | Notes |
|---|---|---|
| `id` | string | Group name (for logging) |
| `destinationDir` | string | References a `directories:` `id` |
| `condition` | string | `notExists` — skip if destination file already present |
| `files` | list | List of `{ source, rename? }` entries |

### 5.8 `releaseFlags:` and `defaultActiveFlags:`

```yaml
releaseFlags:
  - PerUser
  - PerMachine

defaultActiveFlags:
  - PerUser
```

When flags are active, any field that accepts a **conditional map** resolves to
the value matching the first active flag, or `_else` as fallback:

```yaml
installScope:
  PerUser:    perUser
  PerMachine: perMachine
  _else:      perUser
```

Conditional maps are supported on: `product.installScope`, `product.installDir`,
`directories[].path`, `environment[].scope`, `environment[].value`.

### 5.9 `wix:`

Raw WiX XML escape hatch. Fragments are injected into the generated Wix document
before compilation.

```yaml
wix:
  fragments:
    - inline: "<Fragment>...</Fragment>"
    - file: "installer/custom.wxs"
```

---

## 6. Path Resolution

Resolution order for a `source:` value:

1. Replace `[YamlDir]`, `[GitRoot]`, `[CurrentDir]` tokens
2. Replace `aliasName:` prefix with alias value
3. If relative, make absolute relative to the YAML file directory
4. If contains `**`: split at `**`, enumerate recursively, preserve relative paths
5. If contains `*` or `?`: enumerate with `Directory.GetFiles(dir, pattern)`
6. Otherwise: treat as a literal file path

---

## 7. Report Mode (`--report`)

Produces a human-readable content summary without invoking the WiX compiler.
Output includes:

- Product metadata (name, version, upgrade code, active flags)
- Complete file tree as `[Folder]` / `[File]` hierarchy
- Environment variables with resolved scope and value
- File groups with destination paths and condition notes
- WiX fragment sources (file path or first line of inline XML)

---

## 8. Feature Test Suite

Feature tests live under `test/features/`. Each subdirectory contains:
- `allie-pack.yaml` — demonstrates one feature
- Supporting files required by the config
- Runs cleanly with `--report` as a smoke test

| Directory | Feature |
|---|---|
| `test/base/` | Minimal single-file MSI |
| `test/features/env-var/` | Environment variables |
| `test/features/named-dirs/` | Named directories + file groups |
| `test/features/recursive-source/` | `**` glob with subdirectory preservation |
| `test/features/condition-not-exists/` | `condition: notExists` on a group |
| `test/features/release-flags/` | PerUser / PerMachine conditional maps |

---

## 9. Known Limitations (current version)

- Windows only; requires .NET Framework 4.8.1 on build machine
- WiX compiler must be present (installed via `WixSharp_wix4.bin` NuGet)
- Registry keys, Windows Services, IIS, ODBC — not yet implemented; use `wix: fragments:` as interim
- Per-user shortcuts require the installer to run in the user session; some per-user install scenarios need additional WiX configuration
- Solution resolver requires MSBuild outputs to be present; does not invoke a build
