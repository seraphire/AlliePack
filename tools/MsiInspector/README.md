# MsiInspector

Read-only MSI inspection library and CLI for AlliePack test infrastructure.

Lives at `AlliePack/tools/MsiInspector/`. Used by AlliePack tests to validate that AlliePack-produced installers contain the expected content (files in correct directories, components reference correct paths, no duplicate Directory rows, etc.) without actually running the installer.

## Why this exists

When AlliePack's folder-resolution code changes — for example, allowing multiple YAML sections to declare the same folder, or supporting nested folder declarations — we need automated validation that the resulting `.msi` is correct. Running the installer to check is slow, requires elevation, and pollutes the test machine. Reading the MSI's database tables directly is fast, deterministic, and side-effect-free.

MsiInspector provides:

- **A library** (`MsiInspector.Inspector`) with typed queries for the common test cases — file by key, directory full-path resolution, file count by directory, etc.
- **A CLI** (`msiinspector.exe`) that dumps an MSI to deterministic IDT files, suitable for golden-file diff testing.

## Architecture

Single project (`src/MsiInspector/`) produces both the library and the CLI exe. AlliePack tests reference the project directly for typed queries; integration tests can also `Process.Start` the CLI for full-MSI snapshot diffs.

A thin shim (`IMsiBackend`) isolates DTF (`WixToolset.Dtf.WindowsInstaller`) so it can be swapped without touching the rest of the codebase. Currently DTF is the only backend.

## DTF dependency posture

This project pins to **`WixToolset.Dtf.WindowsInstaller` v5.0.2** specifically because:

- v5.0.2 is licensed **MS-RL only** (no OSMF EULA on the binary)
- It pre-dates the WiX 6.0.2 OSMF cutover
- It is feature-complete for our read-only needs

The MS-RL license travels with the binary per the standard NuGet license inclusion. MsiInspector's own code can be licensed independently of MS-RL since we only consume DTF as a binary dependency — no MS-RL source is imported into MsiInspector.

## Public API surface

```csharp
using var inspector = new MsiInspector.Inspector("path/to/installer.msi");

// Typed accessors
foreach (var file in inspector.Files) { /* ... */ }
foreach (var dir in inspector.Directories) { /* ... */ }
foreach (var c in inspector.Components) { /* ... */ }
var summary = inspector.Summary;

// Convenience queries for common test cases
var f = inspector.GetFile("appExe");
var fullPath = inspector.ResolveDirectoryFullPath("INSTALLFOLDER");
var count = inspector.CountDirectoryEntries("INSTALLFOLDER");
foreach (var f in inspector.GetFilesIn("INSTALLFOLDER")) { /* ... */ }

// Escape hatch for ad-hoc MSI SQL
foreach (var row in inspector.Query("SELECT `File`, `FileName` FROM `File`"))
{
    var fileKey = (string)row["File"];
}
```

## CLI usage

```pwsh
# List user tables
msiinspector tables installer.msi

# Dump all user tables to IDT files (deterministic; suitable for golden-file diff)
msiinspector dump installer.msi -o ./dump

# Run a raw MSI SQL query
msiinspector query installer.msi "SELECT `File`, `FileName` FROM `File`"
```

## Out of scope

- **Writing to MSIs.** Read-only by design.
- **Custom action execution.** This is not a CA harness.
- **MSI authoring.** AlliePack already does that via WixSharp.
- **An ADO.NET provider, RI extension, or schema cache.** That would be MsiLib territory; see `MsiLib/docs/reviews/_landing.md` for context on why MsiLib is parked.

If a feature request lands here that touches any of the above, that's a re-open-MsiLib decision, not a "feature for MsiInspector" decision.
