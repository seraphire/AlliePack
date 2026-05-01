# Schema Reference

Complete field reference for `allie-pack.yaml`. For conceptual explanations of how these features work together, see the [Schema Guide](schema-guide.md).

---

## Top-level structure

```yaml
product:          # required
aliases:          # optional
paths:            # optional
structure:        # optional (at least one file source expected)
shortcuts:        # optional
environment:      # optional
registry:         # optional
services:         # optional
directories:      # optional
groups:           # optional
features:         # optional
releaseFlags:     # optional
defaultActiveFlags: # optional
signing:          # optional
wixToolsPath:     # optional
wix:              # optional
```

---

## `product:`

| Field | Type | Required | Default | Notes |
|---|---|---|---|---|
| `name` | string | yes | — | Display name shown in Add/Remove Programs |
| `manufacturer` | string | yes | — | Publisher/company name |
| `version` | string or version block | yes | — | Four-part (`1.0.0.0`), from PE file, or from git tags. See [version sourcing](#version-sourcing). |
| `description` | string | no | `""` | Description shown in Add/Remove Programs |
| `upgradeCode` | GUID string | yes | — | Must stay constant across all versions of your product. Changing this breaks upgrades. |
| `installScope` | string or conditional | no | `perMachine` | `perUser`, `perMachine`, or `both` |
| `installDir` | string or conditional | no | `[ProgramFilesFolder]\Manufacturer\Name` | Root install directory |
| `platform` | string | no | `x86` | `x86`, `x64`, or `arm64` |
| `licenseFile` | string | no | — | Path to an `.rtf` file. Adds a license agreement dialog to the installer UI. |

### Version sourcing

**Literal:**
```yaml
version: "1.0.0.0"
```

**From a PE file (reads FileVersionInfo at build time):**
```yaml
version:
  file: "bin:MyApp.exe"
  source: "file-version"    # or "product-version"
```

**From git tags** (produces `Major.Minor.Patch.CommitsSinceTag`):
```yaml
version:
  source: "git-tag"
  tagPrefix: "v"            # optional; matches tags like v1.2.3
```

---

## `aliases:`

Short names for directory paths, used in `source:` fields as `alias:pattern`.

```yaml
aliases:
  bin: "[GitRoot]/src/MyApp/bin/Release/net481"
  assets: "installer/assets"
```

Values may contain built-in tokens. See [Token reference](#built-in-tokens).

---

## `paths:`

Named tokens available as `[name]` throughout the config. Values may contain built-in tokens. Can be overridden on the command line with `--define name=value`.

```yaml
paths:
  srcRoot: "[CurrentDir]"
  buildOutput: "[srcRoot]/bin/Release"
```

---

## `structure:`

List of `StructureElement` entries defining the installed file tree under `INSTALLDIR`.

| Field | Type | Notes |
|---|---|---|
| `folder` | string | Creates a subdirectory. Omit for files at the current level. |
| `source` | string | File path or glob. Supports `alias:pattern` and `**` recursive glob. |
| `solution` | string | Path to a `.sln` file. Resolves all project build outputs. |
| `project` | string | Path to a `.csproj` or `.vbproj` file. Resolves build outputs. |
| `configuration` | string | Build configuration for solution/project resolve. Default: `Release` |
| `platform` | string | Build platform for solution/project resolve. Default: `AnyCPU` |
| `includeProjects` | list | Whitelist of project names to include from a solution. |
| `excludeProjects` | list | Project names to exclude from a solution. |
| `excludeFiles` | list | Glob patterns applied to the resolved file list. |
| `onEmpty` | string | What to do if no files are found: `warn` (default), `error`, or `ignore`. |
| `contents` | list | Nested `StructureElement` list for subdirectories. |

### Source field syntax

| Pattern | Resolves to |
|---|---|
| `bin:MyApp.exe` | File `MyApp.exe` under the `bin` alias path |
| `bin:*.dll` | All `.dll` files directly under `bin` |
| `bin:**/*.dll` | All `.dll` files recursively under `bin`, preserving subdirectory structure |
| `[GitRoot]/output/file.exe` | Absolute path using a built-in token |
| `relative/path/file.exe` | Resolved relative to the YAML file location |

---

## `shortcuts:`

| Field | Type | Notes |
|---|---|---|
| `name` | string | Shortcut display name |
| `target` | string | Path, typically `[INSTALLDIR]\\AppName.exe` |
| `description` | string | Tooltip text |
| `icon` | string | Path to an `.ico` file |
| `folder` | string | Destination — WiX folder property or well-known alias |

### Folder values

| Value | Location |
|---|---|
| `[ProgramMenuFolder]` | Start Menu\Programs (current user) |
| `[CommonProgramMenuFolder]` | Start Menu\Programs (all users) |
| `[DesktopFolder]` | Desktop (current user) |
| `[CommonDesktopFolder]` | Desktop (all users) |
| `startmenu` | Scope-aware: per-user or all-users based on `installScope` |
| `desktop` | Scope-aware: per-user or all-users based on `installScope` |
| `startup` | Scope-aware startup folder |

---

## `environment:`

| Field | Type | Notes |
|---|---|---|
| `name` | string | Variable name |
| `value` | string or conditional | Value; supports `[INSTALLDIR]` and WiX properties |
| `scope` | string or conditional | `user` or `machine`. Default inferred from `installScope`. |

Variables are removed on uninstall.

---

## `registry:`

| Field | Type | Notes |
|---|---|---|
| `root` | string | `HKLM`, `HKCU`, `HKCR`, `HKU` |
| `key` | string | Registry key path under the hive |
| `name` | string | Value name. Omit for the default value. |
| `value` | string or conditional | Value data. Supports `[INSTALLDIR]` and WiX properties. |
| `type` | string | `string` (default), `expandString`, `multiString`, `dword`, `qword`, `binary` |
| `win64` | bool | `true` = 64-bit registry view, `false` = 32-bit (WOW64). Default: matches platform. |

---

## `services:`

| Field | Type | Notes |
|---|---|---|
| `name` | string | Internal service name (used by SCM) |
| `displayName` | string | Human-readable name in Services panel. Defaults to `name`. |
| `description` | string | Description shown in Services panel |
| `executable` | string | Path after install, e.g. `[INSTALLDIR]\\MyService.exe` |
| `arguments` | string | Command-line arguments |
| `account` | string | `LocalSystem` (default), `LocalService`, `NetworkService`, or `DOMAIN\user` |
| `password` | string | Password for domain accounts only |
| `start` | string | `auto` (default), `demand` / `manual`, `disabled` |
| `type` | string | `ownProcess` (default) or `shareProcess` |
| `errorControl` | string | `ignore`, `normal` (default), `critical` |
| `delayedAutoStart` | bool | Delay startup (auto-start services only) |
| `onFailure` | block | Failure recovery actions (see below) |
| `dependsOn` | list | Service or group names this service depends on |

### `services[].onFailure:`

| Field | Type | Notes |
|---|---|---|
| `first` | string | Action on 1st failure: `none` (default), `restart`, `reboot`, `runCommand` |
| `second` | string | Action on 2nd failure |
| `third` | string | Action on 3rd failure |
| `resetAfterDays` | int | Reset failure count after N days of success |
| `restartDelaySeconds` | int | Seconds to wait before restarting after failure |

---

## `directories:`

Named install destinations outside `INSTALLDIR`. Referenced by `id` in `groups:`.

| Field | Type | Notes |
|---|---|---|
| `id` | string | Reference name used in `groups[].destinationDir` |
| `path` | string or conditional | Full path. Ignored when `type:` is set. |
| `type` | string | Well-known location shorthand (see table below) |
| `subPath` | string | Appended to the `type:` base. Required when `type:` is used. |

### Well-known `type:` values

| Type | Per-user path | Per-machine path |
|---|---|---|
| `config` | `%AppData%` | `%ProgramData%` |
| `localdata` | `%LocalAppData%` | `%ProgramData%` |
| `desktop` | User desktop | All-users desktop |
| `startmenu` | User Start Menu | All-users Start Menu |
| `startup` | User Startup folder | All-users Startup folder |
| `psmodules51` | `My Documents\WindowsPowerShell\Modules` | `Program Files\WindowsPowerShell\Modules` |
| `psmodules7` | `My Documents\PowerShell\Modules` | `Program Files\PowerShell\7\Modules` |

---

## `groups:`

Files installed to named directories (outside `INSTALLDIR`).

| Field | Type | Notes |
|---|---|---|
| `id` | string | Group name (used in logging) |
| `destinationDir` | string | References a `directories[].id` |
| `condition` | string | `notExists` — skip file if destination already exists |
| `permanent` | bool | `true` — file survives uninstall |
| `files` | list | List of `{ source, rename? }` entries |

---

## `features:`

Optional selectable installer features shown as checkboxes in the installer UI.

| Field | Type | Notes |
|---|---|---|
| `id` | string | Internal identifier |
| `name` | string | Display name in feature tree |
| `description` | string | Shown when feature is selected in UI |
| `default` | bool | Whether checked by default. Default: `true` |
| `display` | string | `collapse` (default), `expand`, or `hidden` |
| `structure` | list | StructureElement list for this feature's files |
| `shortcuts` | list | Shortcuts installed with this feature |
| `environment` | list | Environment variables for this feature |
| `registry` | list | Registry entries for this feature |
| `services` | list | Services installed with this feature |
| `groups` | list | File groups for this feature |

---

## `releaseFlags:` and `defaultActiveFlags:`

```yaml
releaseFlags:
  - PerUser
  - PerMachine

defaultActiveFlags:
  - PerUser
```

`releaseFlags:` declares which flag names are valid. `defaultActiveFlags:` sets which flags are active when no `--flag` argument is passed. Flags are activated at build time with `--flag <name>`.

When a flag is active, any field that accepts a **conditional map** resolves to the matching value:

```yaml
installScope:
  PerUser:    perUser
  PerMachine: perMachine
  _else:      perUser     # fallback when no matching flag is active
```

Conditional maps are supported on: `product.installScope`, `product.installDir`, `directories[].path`, `environment[].value`, `environment[].scope`, `registry[].value`.

---

## `signing:`

| Field | Type | Notes |
|---|---|---|
| `thumbprint` | string | SHA1 cert thumbprint in Windows cert store |
| `pfx` | string | Path to PFX file (resolved via aliases/tokens) |
| `pfxPassword` | string | PFX password; supports `[TOKEN]` substitution |
| `azure` | block | Azure Trusted Signing config (see below) |
| `command` | string | Shell command; `{file}` is replaced with the file path |
| `timestampUrl` | string | RFC 3161 timestamp server URL |
| `signToolPath` | string | Explicit path to `signtool.exe`. Auto-discovered when omitted. |
| `files` | block | Sign packaged files before WiX packages them |

Exactly one of `thumbprint`, `pfx`, `azure`, or `command` is required.

### `signing.azure:`

| Field | Type | Notes |
|---|---|---|
| `endpoint` | string | Regional endpoint, e.g. `https://eus.codesigning.azure.net` |
| `account` | string | Azure Trusted Signing account name |
| `certificateProfile` | string | Certificate profile name |
| `dlibPath` | string | Path to `Azure.CodeSigning.Dlib.dll` |
| `correlationId` | string | Optional audit tracking value; supports `[TOKEN]` |

### `signing.files:`

| Field | Type | Notes |
|---|---|---|
| `mode` | string | `unsigned` (default) — skip already-signed files. `all` — sign everything. |
| `include` | list | Filename globs. When omitted, the Windows SIP table determines what is signable. |
| `exclude` | list | Filename globs always excluded after the include/SIP check. |

See [Signing](signing.md) for the full reference.

---

## `wixToolsPath:`

```yaml
wixToolsPath: "C:/tools/wix5/bin"
```

Pin AlliePack to a specific WiX installation. Also settable via the `WIXSHARP_WIXLOCATION` environment variable.

---

## `wix:`

Raw WiX XML escape hatch.

| Field | Type | Notes |
|---|---|---|
| `fragments[].inline` | string | Inline WiX XML string |
| `fragments[].file` | string | Path to a `.wxs` file (resolved via aliases/tokens) |

```yaml
wix:
  fragments:
    - inline: |
        <Fragment>
          <Property Id="MYKEY" Value="1" />
        </Fragment>
    - file: "installer/extra.wxs"
```

---

## Built-in tokens

Available in `source:`, `aliases:`, `paths:`, and most string fields.

| Token | Resolves to |
|---|---|
| `[YamlDir]` | Directory containing the YAML config file |
| `[GitRoot]` | Root of the nearest git repository (walks up from YAML dir) |
| `[CurrentDir]` | Working directory when AlliePack is invoked |
| `[KEY]` | Value of `--define KEY=VALUE` or `paths.KEY` |

WiX install-time properties (not build-time tokens) are also valid in path and value fields where noted:

| Property | Resolves to (at install time) |
|---|---|
| `[INSTALLDIR]` | The root installation directory chosen by the user |
| `[ProgramFilesFolder]` | `C:\Program Files (x86)` |
| `[ProgramFiles64Folder]` | `C:\Program Files` |
| `[AppDataFolder]` | `%AppData%` (current user) |
| `[LocalAppDataFolder]` | `%LocalAppData%` (current user) |
| `[CommonAppDataFolder]` | `%ProgramData%` (all users) |
| `[PersonalFolder]` | `My Documents` |
| `[WindowsFolder]` | `C:\Windows` |
| `[SystemFolder]` | `C:\Windows\System32` |
