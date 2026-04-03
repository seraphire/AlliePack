Release Flags Feature Test
==========================
This installer can be built as either a per-user or a per-machine install
from the same config file by passing --flag PerUser or --flag PerMachine.

Build both targets:
  AlliePack.exe allie-pack.yaml --flag PerUser    --output output\test-peruser.msi
  AlliePack.exe allie-pack.yaml --flag PerMachine --output output\test-permachine.msi

Preview both targets:
  AlliePack.exe allie-pack.yaml --flag PerUser    --report
  AlliePack.exe allie-pack.yaml --flag PerMachine --report
