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

### `-D`, `--define KEY=VALUE`

Define a named token that can be referenced anywhere in the YAML as `[KEY]`. Overrides any matching entry in the `paths:` block. Repeatable.

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

### `--scope <value>`

Override the install scope for configs that use `installScope: both`. Values: `perUser`, `perMachine`.

```
AlliePack.exe allie-pack.yaml --scope perUser    --output dist\MyApp-user.msi
AlliePack.exe allie-pack.yaml --scope perMachine --output dist\MyApp-machine.msi
```

### `--keep-wxs`

Preserve the generated `.wxs` WiX source file after building. The file is written alongside the output MSI. Useful for debugging or when you need to inspect what WiX XML AlliePack produced.

```
AlliePack.exe allie-pack.yaml --keep-wxs
```

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
