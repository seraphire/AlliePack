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
| GAP-6 | Portable WXS export (self-contained, no AlliePack required to compile) | Unphased | **Closed** | gms pack --save-wxs art workflow |
| GAP-7 | CLI should accept repeated `--define` flags (QOL) | Unphased | **Open** | LeadView _make_package (Solution + DocRoot) |

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

**Closed.** Implemented as `--export-wxs` (with optional `-o <dir>`).

The export artifact directory contains everything needed to run `wix build` on
any machine with `wix.exe` installed — no AlliePack or WixSharp required:

- Generated `.wxs` with all paths rewritten to be relative to the export directory
- `build.ps1` script (pass `-Version` to stamp the MSI version)
- WixSharp runtime DLLs (only those actually referenced by the WXS are kept;
  unreferenced CA DLLs staged by WixSharp are pruned automatically)
- WiX extension DLLs bundled alongside the WXS where available
- All referenced installer assets copied into the directory

`ProductCode` is omitted from the exported WXS so each `wix build` auto-generates
a fresh one, which triggers Windows Installer's major-upgrade path without
requiring a committed GUID change on every build.

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

---

### GAP-7 -- Repeated `--define` flags (QOL)

**Problem:** `--define` is bound to an `IEnumerable<string>` (Options.cs), and
CommandLineParser rejects the same option name appearing more than once:

```
AlliePack.exe config.yaml --define A=1 --define B=2
  ERROR(S): Option 'D, define' is defined multiple times.
```

Today the only accepted form is space-separated values after a single flag:

```
AlliePack.exe config.yaml --define A=1 B=2
```

The repeated-flag form is the more natural expectation -- especially when a
caller builds the command up programmatically (e.g. a script appending one
`--define KEY=VALUE` per override). It bit the LeadView `_make_package.cmd`
workflow, which needed to inject both `Solution` and `DocRoot`.

**Proposed enhancement:** accept repeated `--define` flags and merge them with
the space-separated form (both should work). Likely implemented by
pre-processing argv to coalesce repeated `-D/--define` occurrences before
CommandLineParser sees them, or by switching to a parser configuration that
allows multiple occurrences of a sequence option.

**Workaround:** pass all tokens after one flag: `--define KEY1=V1 KEY2=V2`.
