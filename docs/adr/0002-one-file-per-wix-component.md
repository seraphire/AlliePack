# One WiX Component per installed file

AlliePack generates exactly one WiX `<Component>` for each file it installs. Users never declare Components in the Package Definition -- they declare Sources, and AlliePack generates the Component structure automatically.

This follows the WiX-recommended "one file per component" rule. Multi-file Components are legal in WiX but cause problems: Windows Installer's repair mechanism operates at the Component level, so a multi-file Component repairs all its files or none of them. Single-file Components give the repair engine precise granularity and avoid partial-repair states.

## Consequences

AlliePack does not expose `<Component>` as a user-facing concept. Registry keys, environment variables, and services are each generated as their own Component alongside the files they relate to, consistent with this rule.
