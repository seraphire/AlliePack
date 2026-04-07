# AlliePack Watch — Functional Specification

**Status:** Pre-development (design phase)
**Proposed project:** Sibling to AlliePack — likely a separate repository
**Last updated:** 2026-04-07

---

## 1. Purpose

AlliePack Watch is a Windows installer analysis tool. It takes a before/after
snapshot of the system around an installation and produces a structured diff of
everything the installer changed — files, registry, services, environment
variables, shortcuts, scheduled tasks, and event log sources.

Primary use cases:
- **Reverse-engineer** a compiled installer when no source project is available
- **Validate** what a legacy installer actually deploys before migrating it
- **Audit** an installer for compliance or security review
- **Generate** an `allie-pack.yaml` as a migration starting point

AlliePack Watch is intentionally useful to anyone working with Windows installers,
not only AlliePack users. The primary output is a tool-agnostic JSON diff. YAML
generation for AlliePack is one of several export targets.

---

## 2. Design Principles

**No real-time monitoring required.**
The snapshot diff approach (before/after) captures the net result of an install —
what was left behind — without kernel hooks, ETW consumers, minifilter drivers,
or signed driver infrastructure. Transient files created and deleted during the
install (staging, extraction buffers, rollback scripts) are not captured; in
practice these are exactly what filter rules remove anyway.

**Filters are post-processing, not a capture gate.**
The raw diff is always saved. Filter layers are applied separately to produce
cleaned output. This means the install runs once; filters can be revised and
re-applied to the same diff indefinitely without repeating the installation.

**Crowdsourced noise reduction.**
Every environment has its own noise profile — AV products, monitoring agents,
enterprise endpoint software. Filter packs are shareable community artifacts.
Because filters operate on saved diffs, a new filter pack immediately improves
all existing captures, not just future ones.

**Traceable output.**
Every entry in the generated YAML is annotated with the observed source value
so the developer understands where it came from and can verify it.

---

## 3. System Overview

```
AlliePack.Watch.exe snapshot  →  before.json  (pre-install snapshot)

  [run installer]

AlliePack.Watch.exe snapshot  →  after.json   (post-install snapshot)

AlliePack.Watch.exe diff before.json after.json  →  raw-diff.json

AlliePack.Watch.exe filter raw-diff.json [--filter ...] [--filters rules.yaml]
                                          →  filtered-diff.json

AlliePack.Watch.exe export filtered-diff.json --format allie-pack
                                          →  allie-pack.yaml
```

Each stage is a separate command so any step can be repeated independently.
The raw diff is the stable artifact; all other stages are derived from it.

---

## 4. CLI Interface

### 4.1 `snapshot`

Take a point-in-time snapshot of the system state.

```
AlliePack.Watch.exe snapshot [options] --output <file>
```

| Option | Description |
|---|---|
| `--output <file>` | Required. Path to write the snapshot JSON. |
| `--include <path>` | Scope filesystem capture to this path (repeatable). Default: all fixed drives. |
| `--exclude <path>` | Exclude a path from filesystem capture (repeatable). |
| `--no-registry` | Skip registry capture. |
| `--no-filesystem` | Skip filesystem capture. |

### 4.2 `diff`

Compute the difference between two snapshots.

```
AlliePack.Watch.exe diff <before> <after> --output <file>
```

Produces a raw diff JSON containing all observed additions, modifications, and
deletions. No filtering is applied at this stage.

### 4.3 `filter`

Apply filter layers to a raw diff to remove noise.

```
AlliePack.Watch.exe filter <diff> [options] --output <file>
```

| Option | Description |
|---|---|
| `--filter <name>` | Apply a named built-in or community filter pack (repeatable). |
| `--filters <file>` | Apply rules from an `allie-watch-filters.yaml` file (repeatable). |
| `--output <file>` | Path to write the filtered diff JSON. |

### 4.4 `export`

Convert a filtered diff to an output format.

```
AlliePack.Watch.exe export <diff> --format <name> --output <file>
```

| Format | Output |
|---|---|
| `allie-pack` | `allie-pack.yaml` for use with AlliePack |
| `report` | Human-readable text report |
| `json` | Re-export the diff as formatted JSON (passthrough) |

Additional export formats (future): `wix`, `inno`, `nsis`.

### 4.5 Convenience shorthand

For common workflows, the steps can be chained:

```
# Before install
AlliePack.Watch.exe watch start --output before.json

# After install
AlliePack.Watch.exe watch finish --before before.json --raw diff.json \
    --filter windows-baseline --filter antivirus \
    --output allie-pack.yaml
```

`watch start` is an alias for `snapshot`. `watch finish` runs `snapshot`,
`diff`, `filter`, and `export` in sequence, saving the raw diff alongside
the final output.

---

## 5. Snapshot Format

The snapshot is a JSON document with the following top-level structure:

```json
{
  "capturedAt": "2026-04-07T14:23:00Z",
  "machine": "HOSTNAME",
  "version": "1.0",
  "filesystem": [ ... ],
  "registry": [ ... ],
  "services": [ ... ],
  "environmentVariables": { ... },
  "scheduledTasks": [ ... ],
  "shortcuts": [ ... ],
  "eventLogSources": [ ... ]
}
```

### 5.1 Filesystem entries

```json
{
  "path": "C:\\Program Files\\MyApp\\myapp.exe",
  "size": 1048576,
  "lastWriteUtc": "2026-04-07T14:20:00Z",
  "sha256": "abc123..."
}
```

### 5.2 Registry entries

```json
{
  "hive": "HKLM",
  "key": "SOFTWARE\\MyCompany\\MyApp",
  "name": "Version",
  "type": "REG_SZ",
  "value": "1.2.3"
}
```

---

## 6. Raw Diff Format

The diff records additions, modifications, and deletions across all captured
categories.

```json
{
  "diffedAt": "2026-04-07T14:25:00Z",
  "beforeSnapshot": "before.json",
  "afterSnapshot": "after.json",
  "filesystem": {
    "added":    [ { "path": "...", "size": 0, "sha256": "..." } ],
    "modified": [ { "path": "...", "before": { ... }, "after": { ... } } ],
    "deleted":  [ { "path": "..." } ]
  },
  "registry": {
    "added":    [ { "hive": "HKLM", "key": "...", "name": "...", "value": "..." } ],
    "modified": [ ... ],
    "deleted":  [ ... ]
  },
  "services":           { "added": [ ... ], "modified": [ ... ], "deleted": [ ... ] },
  "environmentVariables": { "added": { ... }, "modified": { ... }, "deleted": [ ... ] },
  "scheduledTasks":     { "added": [ ... ], "modified": [ ... ], "deleted": [ ... ] },
  "shortcuts":          { "added": [ ... ], "deleted": [ ... ] },
  "eventLogSources":    { "added": [ ... ] }
}
```

The raw diff is the stable, unfiltered record. It should be preserved alongside
any derived outputs.

---

## 7. Filter System

### 7.1 Layer order

Filters are applied in sequence. Each layer reduces the diff; later layers
operate on the already-reduced result.

| Layer | Description |
|---|---|
| 1 | `windows-baseline` — always applied; covers well-known Windows noise locations |
| 2 | Named profiles (`--filter antivirus`, `--filter visual-studio`, etc.) |
| 3 | Project rules files (`--filters allie-watch-filters.yaml`) |
| 4 | Community filter packs (referenced by name from a registry) |

### 7.2 `windows-baseline` coverage

| Category | Patterns |
|---|---|
| Prefetch | `C:\Windows\Prefetch\*.pf` |
| Temp files | `%TEMP%\**`, `%TMP%\**`, `C:\Windows\Temp\**` |
| Windows Search | `C:\ProgramData\Microsoft\Search\**` |
| Thumbnails / icon cache | `%LOCALAPPDATA%\Microsoft\Windows\Explorer\**` |
| WER / crash dumps | `%LOCALAPPDATA%\Microsoft\Windows\WER\**` |
| ETW traces | `C:\Windows\System32\LogFiles\**` |
| Registry hive transaction logs | `*.LOG1`, `*.LOG2` |
| Performance counters | `HKLM\...\Perflib\**` |
| Recent docs | `HKCU\...\Explorer\RecentDocs\**` |
| Font cache | `C:\Windows\ServiceProfiles\LocalService\AppData\Local\FontCache\**` |
| .NET NGEN cache | `C:\Windows\assembly\NativeImages_**` |

### 7.3 Named profiles

| Profile | Covers |
|---|---|
| `antivirus` | Windows Defender quarantine, scan result caches |
| `visual-studio` | VS telemetry, extension markers, `.suo` files |
| `office` | Office MRU, telemetry, template caches |
| `browser` | Chrome/Edge/Firefox cache and update activity |
| `windows-update` | WU staging, CBS logs, SoftwareDistribution churn |

### 7.4 Rules file format

```yaml
# allie-watch-filters.yaml
name: "My Project Filters"       # optional, for logging
description: "..."               # optional

exclude:
  paths:
    - "C:\\ProgramData\\OtherProduct\\**"
    - "%APPDATA%\\MyApp\\logs\\**"
    - "C:\\Windows\\Temp\\**"        # glob patterns; env vars expanded

  registry:
    - "HKCU\\Software\\MyCompany\\Telemetry\\**"
    - "HKLM\\SOFTWARE\\Microsoft\\Tracing\\**"

  services:
    - "MyWatchdogService"            # service names to exclude from diff

  processes:                         # future: process-scoped mode only
    - "MyBackgroundSync.exe"
```

### 7.5 Community filter packs

Community filter packs are YAML files following the rules file format, published
to a central registry (TBD — GitHub-hosted JSON index or NuGet package feed).

```
AlliePack.Watch.exe filter diff.json --filter acme-corp-endpoint --output filtered.json
```

Packs are versioned. The tool caches packs locally and checks for updates.
Because filters run against saved diffs, updating a pack and re-running `filter`
improves existing captures without repeating the install.

---

## 8. Export: `allie-pack` Format

### 8.1 Output structure

The generated `allie-pack.yaml` is annotated with source comments so the
developer can verify each entry:

```yaml
# Watch export -- 2026-04-07T14:30:00Z
# Filtered with: windows-baseline, antivirus
# Review all TODO comments before building.

product:
  name: "MyApp"                        # observed: Add/Remove Programs DisplayName
  manufacturer: "MyCompany"            # observed: Add/Remove Programs Publisher
  version: "1.2.3.0"                   # observed: Add/Remove Programs DisplayVersion
  upgradeCode: ""                      # TODO: set a stable GUID -- none detected

aliases:
  # TODO: replace observed absolute paths with aliases pointing to your build output
  installdir: "C:\\Program Files\\MyApp"

structure:
  - folder: "App"
    contents:
      # Observed: C:\Program Files\MyApp\myapp.exe (1.2 MB)
      - source: "installdir:myapp.exe"   # TODO: replace with build output alias

      # Observed: C:\Program Files\MyApp\config\defaults.ini
      - folder: "config"
        contents:
          - source: "installdir:config\\defaults.ini"

registry:                              # TODO: Phase 1 -- not yet implemented in AlliePack
  # Observed: HKCU\Software\MyCompany\MyApp\InstallPath = C:\Program Files\MyApp
  - root: HKCU
    key: "Software\\MyCompany\\MyApp"
    name: "InstallPath"
    value: "[INSTALLDIR]"
    type: string

environment:
  # Observed: MYAPP_HOME = C:\Program Files\MyApp (machine scope)
  - name: "MYAPP_HOME"
    value: "[INSTALLDIR]"
    scope: machine

shortcuts:
  # Observed: C:\ProgramData\Microsoft\Windows\Start Menu\Programs\MyApp\MyApp.lnk
  - name: "MyApp"
    target: "[INSTALLDIR]\\App\\myapp.exe"
    folder: "[ProgramMenuFolder]"
```

### 8.2 Unsupported features

Diff entries that have no AlliePack equivalent yet are emitted as commented
`wix: fragments:` blocks with the raw observed values preserved:

```yaml
wix:
  fragments:
    # TODO: COM registration -- no AlliePack equivalent yet
    # Observed: HKCR\MyApp.Document\CLSID = {some-guid}
    # - inline: |
    #     <Fragment>
    #       <!-- COM registration for MyApp.Document -->
    #     </Fragment>
```

---

## 9. Relationship to AlliePack

AlliePack Watch and AlliePack are sibling tools with a defined contract:

| Concern | Owner |
|---|---|
| `allie-pack.yaml` schema | AlliePack (authoritative) |
| Raw diff JSON format | AlliePack Watch (authoritative) |
| Export to `allie-pack.yaml` | AlliePack Watch (consumer of AlliePack schema) |
| Filter pack format | AlliePack Watch |
| Filter pack registry | Shared community resource |

AlliePack Watch does not depend on AlliePack being installed. It can export to
`allie-pack.yaml` using an embedded copy of the schema definition.

---

## 10. Platform Requirements

- **OS:** Windows 10 / Windows Server 2019 or later
- **Privileges:** Administrator (required for full registry and service enumeration)
- **Runtime:** TBD — .NET 8 standalone executable preferred (no framework install required on target)
- **No kernel drivers required** — snapshot diff approach uses standard Win32 / .NET APIs only

---

## 11. Out of Scope (v1)

- Real-time process monitoring (ETW / minifilter) — snapshot diff is sufficient
- Linux / macOS support — Windows installer analysis only
- Automatic installer execution — the user runs the installer; Watch takes snapshots around it
- Undo / uninstall generation — Watch records what was installed, not how to remove it
  (though the diff itself could inform an uninstaller)
