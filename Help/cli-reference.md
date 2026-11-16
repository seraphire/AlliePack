# CLI Reference

## Usage

```
AlliePack.exe [config] [options]
```

## Arguments

### `config`

Path to an `allie-pack.yaml` file, or a directory that contains one. Optional — if omitted, AlliePack looks for `allie-pack.yaml` in the current working directory.

```
# All equivalent when allie-pack.yaml is in the current directory:
AlliePack.exe
AlliePack.exe .
AlliePack.exe allie-pack.yaml

# Explicit path:
AlliePack.exe C:\src\myapp\allie-pack.yaml

# Named config (must reference explicitly):
AlliePack.exe myapp.allie.yaml
```

## Options

### `-o`, `--output <path>`

Output path for the generated MSI. Defaults to `<ProductName>.msi` in the current directory.

```
AlliePack.exe allie-pack.yaml --output dist\MyApp-1.0.0.msi
AlliePack.exe allie-pack.yaml -o C:\builds\MyApp.msi
```

The directory must exist. AlliePack does not create it.

### `-r`, `--report`

Dry-run mode. Prints a content report showing what will be in the MSI without building it. Useful for verifying paths, checking what files will be included, and troubleshooting glob patterns before committing to a full build.

```
AlliePack.exe allie-pack.yaml --report
```

Report output includes:
- Product name, version, platform, and install scope
- Complete resolved file tree
- Shortcuts with their targets and destination folders
- Environment variables with resolved scope and value
- File groups with destination directories
- Signing configuration summary (if `signing:` is present)
- WiX fragment sources

### `-v`, `--verbose`

Enable verbose output. In normal mode, AlliePack prints a summary. In verbose mode it prints each resolution step, glob expansion, and signing status for every file.

```
AlliePack.exe allie-pack.yaml --verbose
```

Verbose mode is especially useful when debugging path resolution or signing issues.

### `--debug`

Enable deep diagnostic output. Implies `--verbose` and additionally prints:

- Resolved WiX tool paths (`WixSharpToolDir`, `WixExtensionsDir`, `DtfWindowsInstaller`)
- Fully expanded product config values (name, version, platform, install scope, upgrade code)
- All alias and path token expansions
- Active release flags
- Per-file source-to-destination mappings after glob expansion
- Shortcut target resolution with full `fileMap` dump on miss

```
AlliePack.exe allie-pack.yaml --debug
```

Use `--debug` when `--verbose` is not enough — for example, when a file is missing from the installer, a shortcut target is not found, or a token is not resolving as expected.

### `-D`, `--define KEY=VALUE`

Define a named token that can be referenced anywhere in the YAML as `[KEY]`. Overrides any matching entry in the `variables:` block. Repeatable.

```
# Single define:
AlliePack.exe allie-pack.yaml --define VERSION=2.1.0

# Multiple defines:
AlliePack.exe allie-pack.yaml -D srcRoot=D:\src -D EDITION=Pro

# Inject a signing password from a pipeline secret:
AlliePack.exe allie-pack.yaml --define SIGN_PASSWORD=$(SIGN_PASSWORD)
```

Defines are resolved before YAML parsing, so the substituted value can itself contain YAML-significant characters. Values that will be embedded in paths should use forward slashes or escaped backslashes.

**Common uses:**
- Override `srcRoot` or build output paths without modifying the YAML file
- Inject version numbers in CI (`--define VERSION=$(Build.BuildNumber)`)
- Inject secrets (signing passwords, key vault URLs) from pipeline variables

### `--flag <name>`

Activate a release flag. When a flag is active, any YAML field that contains a conditional map resolves to the value for that flag rather than the default. Repeatable.

```
# Build a per-machine MSI:
AlliePack.exe allie-pack.yaml --flag PerMachine

# Build for a specific customer:
AlliePack.exe allie-pack.yaml --flag ClientA

# Multiple flags:
AlliePack.exe allie-pack.yaml --flag PerMachine --flag InternalTools
```

If no `--flag` is passed, AlliePack uses the `defaultActiveFlags` list from the config. If that list is also empty, conditional maps resolve to their `_else` value.

### `--keep-wxs`

Preserve the generated `.wxs` WiX source file after building. The file is written alongside the output MSI. Useful for debugging or when you need to inspect what WiX XML AlliePack produced.

```
AlliePack.exe allie-pack.yaml --keep-wxs
```

### `--strict`

Turn warnings about content that builds but will fail at install time into errors. Currently this covers a `perUser` package that contains content needing admin rights: per-machine paths such as `[ProgramFiles64Folder]` or `[CommonAppDataFolder]`, `HKLM`/`HKU` registry values, `scope: machine` environment variables, and services. It also covers a file reached from entries with different `sign:` values, which otherwise is signed with the stronger one. Without `--strict` each is a warning.

```
AlliePack.exe allie-pack.yaml --strict
```

### `--sign-files <mode>`

How packaged files are signed: `always` (every signable file), `ifUnsigned` (files with no signature yet) or `never`. Overrides `signing.files.mode` and every entry's `sign:`; `signing.files.exclude` still applies. Turns signing on even when the config has no `signing:` block; the certificate is then picked as described in [Signing](signing.md#choosing-the-certificate-automatically). Without a block, `always` and `ifUnsigned` also sign the MSI.

```
AlliePack.exe allie-pack.yaml --sign-files ifUnsigned
```

### `--sign-package yes|no`

Whether the MSI is signed. Overrides `signing.package.sign`. Without a `signing:` block, `--sign-package yes` alone signs only the MSI.

### `--sign-cert <thumbprint>`

SHA1 thumbprint (40 hex digits) of a certificate in `CurrentUser\My`; anything else is an error. Replaces whatever provider `signing:` and `signing.package:` name (`thumbprint`, `pfx`, `azure` or `command`), so the files and the MSI both use it. A `timestampUrl` set in `signing.package:` is kept. With no `signing:` block it also turns signing on: copies of packaged files without a signature, then the MSI; add `--sign-files never` to sign only the MSI. With a block, the block's `files.mode`, `package.sign` and `inPlace` still apply. Spaces and the invisible mark that certmgr adds when you copy a thumbprint are removed.

```
AlliePack.exe allie-pack.yaml --sign-cert "ab cd ef ..."
```

### `--no-sign`

Sign nothing, even when the config has a `signing:` block. Same as `--sign-files never --sign-package no`; passing it with `--sign-files always`, `--sign-files ifUnsigned` or `--sign-package yes` is an error.

### `--sign-in-place`

Sign the packaged files where they are, instead of signing copies in `obj\AlliePack\<config>\signed`. Same as `signing.inPlace: true`.

### `--export-wxs`

Export a portable, self-contained WXS artifact directory instead of building an MSI. Use `-o` / `--output` to specify the export directory; defaults to `<ProductName>-wxs` in the current directory.

```
AlliePack.exe allie-pack.yaml --export-wxs
AlliePack.exe allie-pack.yaml --export-wxs -o dist\installer-src
```

The export directory contains everything needed to compile the MSI independently with `wix.exe` — no AlliePack or WixSharp installation required on the build machine:

- `<ProductName>.wxs` — WiX source file with all paths relative to the export directory
- `build.ps1` — build script; pass `-Version` to stamp the MSI version
- WixSharp runtime DLLs (only those actually referenced by the WXS)
- Any WiX extension DLLs required by the config (bundled alongside the WXS)
- All referenced installer assets (license file, dialog images, etc.)

**Building from the exported artifact:**

```powershell
cd dist\installer-src
.\build.ps1 -Version 2.1.0
```

Or invoke `wix.exe` directly:

```
wix build MyApp.wxs -d Version=2.1.0 -o MyApp-2.1.0.msi
```

**ProductCode behavior:** The `ProductCode` attribute is omitted from the exported WXS. Each `wix build` invocation auto-generates a fresh ProductCode, which is what triggers Windows Installer's major-upgrade logic. The `UpgradeCode` in your config remains the stable product-family identifier across all versions.

**Typical use case:** CI pipeline at a customer site that does not (and should not) have AlliePack installed. Run AlliePack once in your own build to produce the artifact, commit or publish the artifact directory, then have the customer's pipeline build the versioned MSI from it.

## Environment variables

| Variable | Effect |
|---|---|
| `WIXSHARP_WIXLOCATION` | Path to the directory containing `wix.exe`. Use this in CI environments where multiple WiX versions are installed and you need to pin one without changing the YAML. Takes effect when `wixToolsPath` is not set in the config. |
| `ALLIEPAK_KEEP_WXS` | Set to any value to preserve the generated `.wxs` file. Equivalent to `--keep-wxs`. |

## Config file naming

| Scenario | Filename |
|---|---|
| Default (single config per directory) | `allie-pack.yaml` |
| Named config (multiple configs, one directory) | `<name>.allie.yaml` |

Named configs must be referenced explicitly — AlliePack only searches for `allie-pack.yaml` when no config argument is given.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Build succeeded |
| `1` | Build failed — see console output for details |

In CI pipelines, a non-zero exit code automatically fails the build step.
