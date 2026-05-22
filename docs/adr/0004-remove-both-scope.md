# Remove the "both" install scope

AlliePack's `scope: both` option was intended to produce two Packages (per-machine and per-user) from a single build invocation. It was added as a compromise to avoid requiring duplicate Package Definitions for scope variants.

This option is being removed. No current consumer uses it -- the only known consumer (Great Migrations) always targets per-machine. The use case is already covered by Flags: a pipeline needing both variants runs two CLI invocations with different Flags. The complexity cost of `both` outweighs its convenience benefit for a workflow with no current users.

If a future consumer has a genuine need, the feature can be reintroduced at that time.

## Consequences

Valid scope values are now `perMachine` and `perUser` only. Package Definitions using `scope: both` should be updated to an explicit scope and a Flag-driven build if both variants are needed.
