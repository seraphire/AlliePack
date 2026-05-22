# Compile-time tokens use $(Name) syntax; install-time tokens use [Name] syntax

AlliePack resolves two categories of tokens in a Package Definition:

- **Compile-time tokens** -- resolved by AlliePack on the build machine before the Package is produced. Examples: `$(YamlDir)`, `$(GitRoot)`, user-defined aliases.
- **Install-time tokens** -- resolved by Windows Installer on the end user's machine when the Package runs. Examples: `[INSTALLDIR]`, `[ProgramFilesFolder]`.

These use distinct syntax: `$(...)` for compile-time, `[...]` for install-time. This makes it unambiguous from reading a Package Definition alone whether a token resolves on the build machine or the target machine.

`$(...)` was chosen for compile-time tokens because:
1. It is safe in YAML without quoting (`{...}` would require quoting wherever it starts a value, since `{` is a YAML flow mapping indicator).
2. It mirrors MSBuild property syntax (`$(PropertyName)`), which is the correct mental model for the target audience: compile-time substitution before the build engine runs.
3. It is visually distinct from WiX's fixed `[...]` install-time syntax, which cannot be changed.

## Consequences

Existing Package Definitions using `[YamlDir]` or `[GitRoot]` bracket syntax for compile-time tokens are affected by this change. A migration path (deprecation warning + support for old syntax during a transition period) should be provided before enforcing the new syntax.

User-defined Aliases should adopt the same `$(aliasName):remainder` referencing syntax for consistency.
