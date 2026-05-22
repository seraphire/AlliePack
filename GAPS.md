# AlliePack Gap Tracker

Known feature gaps surfaced by real-world installer configurations. Each gap
maps to a phase in [ROADMAP.md](ROADMAP.md). Gaps are added here as use cases
expose missing functionality; they are closed when the corresponding roadmap
phase is implemented.

---

| ID | Description | Roadmap Phase | Status | Surfaced By |
|---|---|---|---|---|
| GAP-1 | Recursive directory source (`**` glob) | Phase 3 | **Closed** | gms help/book tree |
| GAP-2 | Named directories + file groups (files outside INSTALLDIR) | Phase 3 | **Closed** | gms PS module, config dir |
| GAP-3 | Environment variables (`environment:` block) | Phase 1 | **Closed** | gms GMS_HOME |
| GAP-4 | Conditional file install (`condition: notExists`) | Phase 3 | **Closed** | gms default config |
| GAP-5 | Release flags + scope-variant paths (PerUser/PerMachine) | Phase 4 | **Closed** | gms install scope |
| GAP-6 | Portable WXS export (self-contained, no AlliePack required to compile) | Unphased | **Open** | gms pack --save-wxs art workflow |

---

## Detail

### GAP-1 -- Recursive Directory Source

**Problem:** `source:` globs use `Directory.GetFiles()` which is non-recursive. Any
source tree with subdirectories (e.g., an mdBook output, a pre-built web app, a
localization folder) silently drops everything below the top level.

**Proposed syntax:**
```yaml
- folder: "help"
  contents:
    - source: "help:**/*"    # ** recurses into subdirectories
```

The relative subdirectory structure under the matched root is preserved at the
destination.

---

### GAP-2 -- Named Directories and File Groups

**Problem:** All files in `structure:` are installed relative to `INSTALLDIR`.
There is no way to install files to arbitrary locations such as `%APPDATA%`,
`%ProgramFiles%\WindowsPowerShell\Modules`, or `[PersonalFolder]\Documents`.

**Proposed syntax:**
```yaml
directories:
  - id: PSMODDIR51
    path: "[PersonalFolder]\\WindowsPowerShell\\Modules\\gms"

  - id: CONFIGDIR
    path: "[AppDataFolder]\\GreatMigrations"

groups:
  - id: PsModule
    destinationDir: PSMODDIR51
    files:
      - source: "scripts:gms.psm1"
      - source: "scripts:gms.psd1"
```

`destinationDir` references a named directory ID. Standard WiX folder properties
(`[AppDataFolder]`, `[PersonalFolder]`, `[CommonAppDataFolder]`, etc.) are
supported as path roots and resolve correctly at install time.

---

### GAP-3 -- Environment Variables

**Problem:** No mechanism exists to set user or machine environment variables
during installation. Currently requires a post-install manual step or a
separate script.

**Proposed syntax:**
```yaml
environment:
  - name: "GMS_HOME"
    value: "[INSTALLDIR]"
    scope: user    # user or machine
```

WixSharp has direct support for environment variable components; this is a
schema and wiring addition only.

---

### GAP-4 -- Conditional File Install

**Problem:** Files installed via `groups:` are always written, overwriting any
user-modified version on upgrade. Default config files should be written only
on first install.

**Proposed syntax:**
```yaml
groups:
  - id: DefaultConfig
    destinationDir: CONFIGDIR
    condition: notExists
    files:
      - source: "installer/gms.config.ini"
```

`condition: notExists` skips the component if the destination file is already
present. Implemented as a WiX component condition.

---

### GAP-6 -- Portable WXS Export

**Problem:** The WXS emitted by AlliePack (via WixSharp) contains references to
WixSharp's custom action DLLs (`WixSharp.CA.dll`, `WixSharp.UI.CA.dll`). These
DLLs are placed in the artifact directory alongside the WXS, but the paths
embedded in the WXS may be absolute or otherwise tied to the build machine.
As a result, compiling the WXS on a different machine — or without AlliePack
installed — may fail.

**Use case:** `gms pack` saves the WXS and collateral in an artifact subdirectory
as a WiX workspace that can be compiled with `wix build` independently. For this
to work on a client's machine (where AlliePack is not installed), all DLL
references in the WXS must be relative paths that resolve within the artifact
directory itself.

**What needs to change:**
- All `WixSharp.CA.dll` and `WixSharp.UI.CA.dll` references in the emitted WXS
  must use paths relative to the WXS file (not absolute machine paths)
- The artifact directory must be fully self-contained: everything needed to run
  `wix build <name>.wxs` is present in that directory
- This is significant because WixSharp controls how it emits CA references;
  achieving relative paths requires either post-processing the WXS or hooking
  into WixSharp's project configuration before build

**Proposed behaviour:**
- A `--portable-wxs` flag (or always-on behaviour) that post-processes the
  emitted WXS to rewrite any absolute CA DLL paths to relative equivalents
- Verification step: after rewriting, confirm all referenced files exist
  relative to the WXS before reporting success

---

### GAP-5 -- Release Flags and Scope-Variant Paths

**Problem:** `installScope`, `installDir`, directory paths, and environment
variable scopes are static values. There is no way to produce a per-user and
a per-machine MSI from a single config file, or to vary any field by build
target.

**Proposed syntax:**
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
    PerUser:    "[LocalAppDataFolder]\\Programs\\GreatMigrations\\gms"
    PerMachine: "[ProgramFiles]\\GreatMigrations\\gms"
    _else:      "[LocalAppDataFolder]\\Programs\\GreatMigrations\\gms"

directories:
  - id: PSMODDIR51
    path:
      PerUser:    "[PersonalFolder]\\WindowsPowerShell\\Modules\\gms"
      PerMachine: "[ProgramFiles]\\WindowsPowerShell\\Modules\\gms"
      _else:      "[PersonalFolder]\\WindowsPowerShell\\Modules\\gms"

environment:
  - name: GMS_HOME
    value: "[INSTALLDIR]"
    scope:
      PerUser:    user
      PerMachine: machine
      _else:      user
```

Build both targets:
```
AlliePack.exe allie-pack.yaml --flag PerUser    -D VERSION=1.2.0 --output MyApp-user.msi
AlliePack.exe allie-pack.yaml --flag PerMachine -D VERSION=1.2.0 --output MyApp-machine.msi
```

The conditional map syntax (`FlagName: value`, `_else: fallback`) applies to
any field that currently accepts a scalar: `installScope`, `installDir`,
`directories[].path`, `environment[].scope`, `environment[].value`, and
`registry[].value`.
