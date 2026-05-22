# WXS export resolves all values at export time; no WiX preprocessor variables

When AlliePack exports a portable WXS artifact directory (`--export-wxs`), all values are fully resolved by AlliePack at export time -- version, source paths, and any other environment-specific values. The resulting WXS contains no WiX preprocessor variables (`$(var.Name)`).

Environment-specific values are supplied via `--define KEY=VALUE` on the CLI, using the same compile-time token mechanism as standard builds. Authors use `$(Variable)` references throughout the Package Definition; callers override them for the target environment at invocation time.

Source paths in the exported WXS are written as relative paths from the output directory to the source files, rather than absolute paths.

**Version is the one exception.** Because the exported WXS is intended to feed a customer CI/CD pipeline that supplies the version per build run, version is emitted as a WiX preprocessor variable (`$(var.Version)`) rather than resolved at export time. The customer pipeline passes it at `wix.exe` compile time: `wix build AviTrack.wxs -d Version=2.5.0`. The bundled `build.ps1` exposes `$Version` as a mandatory parameter.

## Considered Options

- **WiX preprocessor variables** -- the WXS would contain `$(var.SrcRoot)`, `$(var.Version)`, etc., resolved by the customer at `wix.exe` compile time. Rejected: introduces a second substitution system the customer must understand, creates opportunity for version mismatches, and requires the customer to know which variables to set.
- **Resolve at export time** (chosen) -- AlliePack resolves everything; the WXS is a fully-specified artifact for a specific version and environment. The customer runs `wix.exe` with no additional parameters beyond what the bundled `build.ps1` provides.

## Consequences

Each exported WXS artifact is specific to the source paths used when AlliePack generated it, but version-agnostic -- the same WXS can be compiled by the customer pipeline for any version. To use different source paths, re-run AlliePack with the appropriate `--define` overrides.
