# Troubleshooting

Common errors and how to fix them. Start with `--report` and `--verbose` — they reveal most issues before you even attempt a build.

---

## File and path problems

### No files found / empty MSI

**Symptom:** The report shows no files, or the MSI is produced but installs nothing.

**Check:** Run `--report --verbose` to see every path that was resolved:

```
AlliePack.exe allie-pack.yaml --report --verbose
```

**Common causes:**

- The `bin` alias path doesn't exist or points to the wrong directory. Print the resolved path: `AlliePack.exe --report --verbose` will show what each alias resolved to.
- The glob pattern doesn't match. `bin:*.dll` only matches files directly in the `bin` directory. Use `bin:**/*.dll` for recursive matching.
- The project hasn't been built yet. AlliePack doesn't build projects — it reads the output of an already-completed build. Run your build first.
- The `configuration` or `platform` in a `project:` or `solution:` entry doesn't match what was built. The platform must match exactly: `AnyCPU` not `Any CPU`.

**Tip:** Add `onEmpty: error` to a `structure:` element to turn "no files found" from a warning into a hard failure:

```yaml
- source: "bin:*.dll"
  onEmpty: error
```

---

### "Path not found" or "File does not exist"

**Symptom:** Build fails with a message about a file or directory not existing.

**Check:** Run `--report` to see what AlliePack resolved the path to. The resolved path will tell you where it looked.

**Common fixes:**

- If the path uses `[GitRoot]`, make sure AlliePack is running inside (or below) a git repository.
- If the path uses `[CurrentDir]`, make sure you're running AlliePack from the expected working directory, or override with `--define srcRoot=<path>`.
- Forward slashes and backslashes both work in most fields, but some contexts are picky. Use `[YamlDir]` instead of relative paths when possible — it's always resolved correctly.

---

### Solution/project resolve finds no outputs

**Symptom:** `solution:` or `project:` resolves but finds zero files.

**Causes:**

- The project hasn't been built. AlliePack reads build outputs; it doesn't run MSBuild. Build the solution first.
- The `configuration` doesn't match. The default is `Release`. If your build used `Debug`, change the config or build with Release.
- The `platform` value doesn't match. Use `AnyCPU` (no space) for projects built as "Any CPU" in Visual Studio.

---

## WiX compilation errors

### "wix.exe not found"

AlliePack couldn't locate `wix.exe`.

**Fix:**

1. Install WiX: `dotnet tool install --global wix --version 5.*`
2. Make sure the dotnet tools directory is on your PATH: typically `%USERPROFILE%\.dotnet\tools`
3. Open a new terminal after installation (PATH changes require a new shell)
4. Or set the location explicitly: `wixToolsPath: "C:/path/to/wix/bin"` in your config, or the `WIXSHARP_WIXLOCATION` environment variable

---

### "Duplicate component GUID" or "Duplicate file"

**Symptom:** WiX compilation fails with a duplicate GUID or duplicate file error.

**Cause:** The same file is being added to the installer twice — typically because it's matched by two different `source:` patterns, or by both a `solution:` entry and an explicit `source:`.

**Fix:** Check for overlapping patterns in your `structure:` block. Use `excludeFiles:` to suppress duplicates, or consolidate to a single source for each file.

---

### WiX compiler error messages

When WiX fails, AlliePack prints the error output from the WiX compiler. Use `--keep-wxs` to preserve the generated `.wxs` file:

```
AlliePack.exe allie-pack.yaml --keep-wxs
```

Then open the `.wxs` file to see the full WiX XML. The error message will reference a line number in that file, which you can use to trace back to the YAML that produced it.

For complex WiX issues, the [WiX documentation](https://wixtoolset.org/docs/) and the [WiX GitHub issues](https://github.com/wixtoolset/wix) are the best resources.

---

## Signing problems

### "signtool.exe not found"

AlliePack couldn't locate `signtool.exe`.

**Fix options:**

1. Install the Windows SDK (includes signtool)
2. Add the signtool directory to PATH
3. Specify the path explicitly: `signing.signToolPath: "C:/path/to/signtool.exe"`

On Azure DevOps hosted agents, signtool is already on PATH via the Windows SDK — no action needed.

---

### "Code signing failed (signtool returned non-zero)"

**Check the output** — AlliePack prints the full signtool error above the failure line. Common causes:

- **Certificate not found:** The thumbprint doesn't match any certificate in the cert store. Verify with `Get-ChildItem Cert:\CurrentUser\My` or `Cert:\LocalMachine\My`.
- **Private key not accessible:** The certificate is installed but the private key is in a different store or has restricted access. On CI agents, ensure the cert is imported for the correct user/account.
- **Wrong timestamp server:** The timestamp server is unreachable or returned an error. Try a different server (`http://timestamp.digicert.com`, `http://timestamp.globalsign.com/scripts/timstamp.dll`), or omit `timestampUrl` for local testing.
- **File is locked:** The file being signed is open by another process. Check for antivirus scanning or file locks.

---

### Signature shows "NotSigned" after build

**Symptom:** Build reports success, but `Get-AuthenticodeSignature` returns `NotSigned`.

**Causes:**

- Signing was configured but the MSI path was wrong — AlliePack warns and skips signing rather than failing if the MSI can't be found.
- The `signing:` block is present but no provider was configured (all of `thumbprint`, `pfx`, `azure`, `command` are empty). AlliePack will error on this.
- The sign step ran but signed a different file than the one you're checking.

---

### Azure Trusted Signing: authentication error

**Symptom:** Signing fails with an authentication or authorization error from the Azure service.

**Check:**

- The pipeline identity has the **Certificate Profile Signer** role on the Trusted Signing resource (not just Reader)
- The `endpoint` matches the region of your Trusted Signing resource
- The `account` and `certificateProfile` names are spelled correctly (case-sensitive)
- The Azure Artifact Signing Client Tools are installed and `dlibPath` points to the right architecture (`x64` vs `x86`)

---

## Version problems

### "Invalid version string"

MSI version numbers must be in four-part format: `Major.Minor.Patch.Build` (e.g., `1.0.0.0`). Each part must be a non-negative integer.

- If using git-tag sourcing: the tag must be in `vX.Y.Z` format (or match your `tagPrefix`). A tag of `v1.2` produces `1.2.0.0`.
- If injecting via `--define`: make sure the value is a valid four-part version.
- If using file-version sourcing: the PE file must have a valid `FileVersion` property set.

---

### Git-tag version returns "0.0.0.N"

**Cause:** No matching git tag was found. AlliePack falls back to `0.0.0.<total commits>`.

**Fix:** Create a tag matching your `tagPrefix`:

```
git tag v1.0.0
```

Or if you're in CI without tags, create the tag before running AlliePack.

---

## Upgrade problems

### Installer overwrites without prompting / doesn't detect previous version

**Cause:** The `upgradeCode` in your config doesn't match the one used in a previous version.

The `upgradeCode` must be the **same GUID across all versions** of your product. If it changes, Windows treats the new installer as a completely different product and doesn't detect the existing installation.

**Fix:** Restore the original `upgradeCode`. Document it prominently in your config file or in a comment.

---

### "Another version is already installed" error

**Cause:** The MSI package code (auto-generated by WiX) conflicts with a previously installed version. This typically happens when testing — you install an MSI, modify something, and try to install the modified MSI without uninstalling first.

**Fix for development:** Uninstall the previous version before reinstalling. For production, increment the product version — MSI upgrades require a higher version number.

---

## Getting more information

**`--report`** — Shows the resolved file tree and config without building. Fast and safe to run repeatedly.

**`--verbose`** — Prints every resolution step, including individual file decisions during signing.

**`--keep-wxs`** — Preserves the generated WiX XML for inspection when WiX compilation fails.

**Event Viewer** — For service installation failures, check `Windows Logs > Application` and `Windows Logs > System` in Event Viewer on the target machine.

**`install.log`** — Run the MSI with logging enabled to capture all MSI engine actions:

```
msiexec /i MyApp.msi /l*v install.log
```

The log captures every action, property value, and error message from the Windows Installer engine.
