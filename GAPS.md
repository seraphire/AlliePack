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
| GAP-8 | Exported `WixSharp.CA.dll` is non-deterministic (churns every export) | Unphased | **Closed** (via GAP-9) | LeadView committed pack/ workflow |
| GAP-9 | Strip WixSharp runtime CA when unused (default, no managed CAs) -- pure-WiX WXS, smaller MSI | Unphased | **Closed** | LeadView (standard UI, no custom actions) |
| GAP-10 | Shortcut without `description:` emits empty `Description=""` (WiX0006) | Unphased | **Open** | Drawing06 (description-less shortcuts) |

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

---

### GAP-8 -- Non-deterministic `WixSharp.CA.dll` in the exported artifact

**Problem:** `--export-wxs` stages `WixSharp.CA.dll` into the export directory,
and WixSharp produces it non-deterministically -- two exports from identical
inputs yield a binary that is the same size but differs by a few bytes (MVID /
timestamp region in the assembly metadata). In the committed-artifact workflow
(GAP-6), where the export directory is checked into git so CI can compile it
without AlliePack, this means every `--export-wxs` run dirties
`WixSharp.CA.dll` even though it is functionally identical:

```
LeadView committed pack/: WixSharp.CA.dll -- 4 bytes differ each run, same size
```

**Impact:** harmless but noisy. Teams must either re-commit an identical-but-
different DLL on every delivery run, or routinely discard the trivial change
(`git checkout -- delivery/pack/WixSharp.CA.dll`) and only re-commit when the
AlliePack/WixSharp version actually changes.

**Proposed direction:** make the staged CA DLL deterministic (e.g. copy a fixed
NuGet-provided binary rather than a freshly stamped one), or document the
discard-on-routine-run guidance. Largely moot for projects that can adopt GAP-9.

**Resolution (2026-06-10):** closed via GAP-9. Installers without managed custom
actions no longer stage `WixSharp.CA.dll` at all -- two consecutive exports are
byte-identical (verified by hash comparison). Custom-UI / managed-CA exports
still stage the non-deterministic DLL; if that combination ever meets a
committed-artifact workflow in practice, reopen as a narrower gap.

---

### GAP-9 -- Strip the WixSharp runtime CA when unused (pure-WiX output)

**Problem:** WixSharp's `Compiler.BuildWxs` unconditionally emits a
`WixSharp_InitRuntime_Action` custom action backed by `<Binary
SourceFile="WixSharp.CA.dll" />` (plus `Software\WixSharp\Used` registry
markers), regardless of whether the project defines any managed custom actions.
For a standard-UI installer with no managed CAs -- the common AlliePack case --
this is dead scaffolding that nonetheless forces `WixSharp.CA.dll` to be
embedded in the MSI and committed alongside the exported WXS.

This is the same limitation first surfaced when evaluating whether AlliePack
could emit a CA-free WXS: the runtime CA is part of WixSharp's default output.

**Use case:** the LeadView installer (`ui: standard`, no custom actions) ships
an embedded `WixSharp.CA.dll` and a WixSharp init CA it never exercises. A truly
WixSharp-free MSI would need none of it.

**Proposed enhancement:** when the resolved project defines no managed custom
actions and does not use `ui: custom`, post-process the generated WXS to remove
the WixSharp runtime CA, its `<Binary>`, the matching `InstallExecuteSequence`
entry, and the `Software\WixSharp\Used` registry markers -- yielding a pure-WiX
WXS with no `WixSharp.CA.dll` dependency. Needs validation that nothing else in
the WixSharp output relies on the init action. Would also resolve GAP-8 for
these projects (no CA DLL -> no churn).

**Scope decisions (2026-06-10):**
- Default behavior, not an opt-in option. Per the progressive-complexity
  principle, a config that doesn't use managed CAs should get the pure-WiX
  output automatically -- no new YAML key required.
- The rule is need-based, not a feature checklist: keep the WixSharp runtime
  if and only if something in the resolved project actually depends on it.
  Today that means a managed custom action or `ui: custom` (WixSharp WPF
  EmbeddedUI, which needs `WixSharp.UI.CA.dll`); if a future feature (e.g. a
  `customActions:` block) introduces a managed CA, it creates the need and the
  runtime stays -- the strip logic should detect dependence rather than
  enumerate features.
- Detection happens at the artifact level: scan the generated WXS for
  references to the `WixSharp.CA.dll` `<Binary>`. If the only referent is the
  init-action scaffolding, strip the binary, the CA, its sequence entry, and
  the `Software\WixSharp\Used` registry markers; if any other custom action
  references the binary, keep everything. This is the validation step for
  which DLLs are actually needed -- self-maintaining, no config-level
  knowledge required.
- Applies to both the direct MSI build path and `--export-wxs`. The export
  directory no longer stages `WixSharp.CA.dll` (eliminating the per-run binary
  churn of GAP-8), and the built MSI no longer embeds it (smaller installer).

**Resolution (2026-06-10):** implemented as
`InstallerBuilder.StripWixSharpRuntime`, registered as a `WixSourceGenerated`
handler on every build path (after the `wix:` fragment handler so injected
fragments participate in need detection). The strip removes the init
`CustomAction`, its sequence `Custom` entries (dropping a sequence element left
empty), the `WixSharp.CA.dll` `<Binary>`, and the `Software\WixSharp\Used`
registry markers. A marker that served as its component's KeyPath is replaced
by promoting `KeyPath="yes"` onto the component's first `File`; file-less
(`CreateFolder`-only) components fall back to the directory keypath by
omission. Exports also prune the now-orphaned `CustomAction.config`. The
runtime is kept untouched when any other CA references the runtime binary, any
other `*.CA.dll` binary exists, or an `EmbeddedUI` element is present.
Verified: unit tests (`WixSharpRuntimeStripTests`), base-test E2E
install/extract, exported artifact compiles standalone with `wix.exe`, and two
consecutive exports are byte-identical.

---

### GAP-10 -- Description-less shortcut emits empty `Description=""`

**Problem:** `AttachShortcut` (InstallerBuilder.cs) sets the shortcut description
unconditionally:

```csharp
var shortcut = new FileShortcut(s.Name, folder) { Description = s.Description };
```

When a `shortcuts:` entry omits `description:`, `s.Description` is null/empty and
the generated WXS gets `Description=""`.  WiX 5 rejects that:

```
error WIX0006: The Shortcut/@Description attribute's value cannot be an empty
string. If a value is not required, simply remove the entire attribute.
```

So a perfectly valid config (shortcut with just `name`/`target`/`folder`) produces
a WXS that fails `wix build`.  It bit the Drawing06 installer, whose shortcuts had
no descriptions.

**Proposed fix:** only assign the description when it is non-empty, e.g.

```csharp
var shortcut = new FileShortcut(s.Name, folder);
if (!string.IsNullOrEmpty(s.Description)) shortcut.Description = s.Description;
```

**Workaround:** give every shortcut a non-empty `description:` in the YAML.
