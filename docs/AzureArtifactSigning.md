# Azure Artifact Signing — Conversation Summary

## Overview

Azure Artifact Signing (formerly Trusted Signing) is a fully managed code-signing service that ensures the authenticity and integrity of your applications. It supports digest-based signing, meaning your binaries never leave your environment.

---

## How to Sign an Executable Using Azure Artifact Signing

### 1. Set Up Azure Artifact Signing

- Create an Artifact Signing resource in the Azure portal.
- Configure a certificate profile (Public Trust, Private Trust, or Test Trust).

### 2. Integrate With Your Development Environment

Azure Artifact Signing works with:

- Visual Studio
- GitHub Actions
- Azure DevOps
- Command-line signing via `signtool.exe` using the Azure Artifact Signing endpoint

### Using signtool.exe With Azure Artifact Signing

**Prerequisites:**

- An Artifact Signing account with identity validation
- Certificate Profile Signer role
- Windows 10 (1809+) or Windows Server 2016+
- Windows SDK (min version: 10.0.2261.755)
- .NET 8 Runtime
- Microsoft Visual C++ Redistributable
- Artifact Signing Client Tools (`Azure.CodeSigning.Dlib.dll`)

Install client tools via WinGet:

```powershell
winget install -e --id Microsoft.Azure.ArtifactSigningClientTools
```

### 3. Create `metadata.json`

```json
{
  "Endpoint": "https://eus.codesigning.azure.net",
  "CodeSigningAccountName": "<YourArtifactSigningAccountName>",
  "CertificateProfileName": "<YourCertificateProfileName>",
  "CorrelationId": "<OptionalCorrelationId>"
}
```

**Field meanings:**

- `Endpoint` -- must match your Azure region
- `CodeSigningAccountName` -- your Artifact Signing account
- `CertificateProfileName` -- the certificate profile you created
- `CorrelationId` -- optional tracking value (e.g., build ID)

**Regional endpoints include:** Brazil South, Central US, East US, Japan East, Korea Central, North Europe, Poland Central, South Central US, Switzerland North, West Europe, West US, West US 2, West US 3, etc.

### 4. Sign the Executable

```powershell
<Path to SDK bin>\x64\signtool.exe sign /v /debug /fd SHA256 `
    /tr "http://timestamp.acs.microsoft.com" /td SHA256 `
    /dlib "<Path to Artifact Signing dlib>\x64\Azure.CodeSigning.Dlib.dll" `
    /dmdf "<Path to metadata.json>" <FileToSign>
```

**Notes:**

- `/dlib` points to `Azure.CodeSigning.Dlib.dll`
- `/dmdf` points to your metadata file
- Timestamping is required because certificates are valid for 3 days

### 5. Verify the Signature

```powershell
signtool verify /pa <FileToSign>
```

---

## Pricing & What You Need to Purchase

Azure Artifact Signing is **not free** and is **not included** in the Azure Free Tier. You must create an Artifact Signing resource and choose a plan.

### Pricing Plans

**Basic -- $9.99/month**

- 5,000 signatures included
- $0.005 per additional signature
- 1 Public Trust + 1 Private Trust certificate profile

**Premium -- $99.99/month**

- 100,000 signatures included
- $0.005 per additional signature
- 10 Public Trust + 10 Private Trust profiles

**Important notes:**

- Billing starts immediately when the resource is created
- Pricing is not prorated
- Free-tier credits can be used during the initial 30-day $200 credit window
- After credits expire, you must upgrade to Pay-As-You-Go

### Can You Use Artifact Signing With an Azure Free Account?

Yes -- but with limitations:

- Artifact Signing is not free
- You can create the resource, but it requires selecting a paid plan
- Free credits can temporarily cover the cost
- After credits expire, you must upgrade to Pay-As-You-Go to continue using it

---

## Q: Is there a way to sign files that does not use signtool, or should I always use it?

**Short answer:** Yes, you can sign files without using `signtool.exe` -- but only if you implement the signing workflow directly using the Azure Artifact Signing REST API or the .NET client library. `signtool.exe` is not required; it's just the easiest integration.

### Two Ways to Sign Files with Azure Artifact Signing

#### 1. Using `signtool.exe` (the simple, Microsoft-supported path)

- You generate a digest locally
- `signtool` calls the Azure Artifact Signing dlib
- Azure returns a signed digest
- `signtool` embeds the signature into the PE file

This is the recommended method for Windows PE signing because it handles:

- PE/Authenticode embedding
- Timestamping
- Catalog signing
- Multi-signature scenarios
- Signature verification rules

#### 2. Using the Azure Artifact Signing REST API or .NET SDK (no signtool)

The lower-level, fully programmatic approach. What you do manually:

1. Compute the file digest (SHA-256) in C#
2. Call the Azure Artifact Signing API to request a signature
3. Receive the signed digest
4. Embed the signature into the executable yourself

**The catch:** Embedding an Authenticode signature into a PE file is non-trivial. You must:

- Parse the PE header
- Create a `WIN_CERTIFICATE` structure
- Insert it into the correct section
- Update checksum fields
- Handle padding and alignment
- Support timestamping (RFC 3161)
- Support dual-signing (SHA1 + SHA256) if needed

**When would you use the API directly?**

- You're building a custom signing service
- You're signing non-Windows artifacts (NuGet, npm, JAR, MSI catalogs)
- You want full automation without external tools
- You're embedding signing into a cloud workflow

### Practical Recommendation

For a C# application that signs Windows executables:

- **Best choice:** Call `signtool.exe` from your C# app
- **Alternative:** Use the Azure Artifact Signing REST API + a PE signing library
- **Worst choice:** Implement PE signing manually

---

## Q: Can you write a method that handles the round-trip signing of an executable?

### C# Method: Sign an Executable Using `signtool.exe` + Azure Artifact Signing

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

public static class AzureSignTool
{
    public static async Task<bool> SignFileAsync(
        string signtoolPath,
        string fileToSign,
        string dlibPath,
        string metadataJsonPath,
        string timestampUrl = "http://timestamp.acs.microsoft.com")
    {
        if (!File.Exists(signtoolPath))
            throw new FileNotFoundException("signtool.exe not found", signtoolPath);

        if (!File.Exists(fileToSign))
            throw new FileNotFoundException("File to sign not found", fileToSign);

        if (!File.Exists(dlibPath))
            throw new FileNotFoundException("Azure.CodeSigning.Dlib.dll not found", dlibPath);

        if (!File.Exists(metadataJsonPath))
            throw new FileNotFoundException("metadata.json not found", metadataJsonPath);

        var args =
            $"sign /v /debug /fd SHA256 " +
            $"/tr \"{timestampUrl}\" /td SHA256 " +
            $"/dlib \"{dlibPath}\" " +
            $"/dmdf \"{metadataJsonPath}\" " +
            $"\"{fileToSign}\"";

        var psi = new ProcessStartInfo
        {
            FileName = signtoolPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };

        process.Start();

        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        bool success = process.ExitCode == 0;

        Console.WriteLine(stdout);
        if (!success)
            Console.WriteLine(stderr);

        return success;
    }
}
```

### How to Call It

```csharp
bool ok = await AzureSignTool.SignFileAsync(
    signtoolPath: @"C:\Program Files (x86)\Windows Kits\10\bin\x64\signtool.exe",
    fileToSign: @"C:\build\MyApp.exe",
    dlibPath: @"C:\AzureSigningTools\x64\Azure.CodeSigning.Dlib.dll",
    metadataJsonPath: @"C:\AzureSigningTools\metadata.json"
);

if (!ok)
{
    Console.WriteLine("Signing failed");
}
else
{
    Console.WriteLine("Signing succeeded");
}
```

### Notes & Best Practices

**`signtool` exit codes:**

- `0` = success
- `1` = generic failure
- `2` = file not found / invalid args
- `3` = signing failed (Azure error, timestamp error, etc.)

**Recommendations:**

- Wrap in a retry policy -- Azure Artifact Signing occasionally returns transient errors (429, 503). Use exponential backoff.
- Capture logs for auditing: `stdout`, `stderr`, correlation ID (from `metadata.json`), timestamp.
- Sign multiple files in parallel by calling the method for each file.

---

## Q: Do I need to compute the SHA by hand and call the webservice by hand?

**No** -- you do **NOT** need to compute the SHA digest yourself, and you do **NOT** need to call the Azure Artifact Signing web service yourself.

When you use `signtool.exe` with the Azure Artifact Signing `Azure.CodeSigning.Dlib.dll` and your `metadata.json`, the entire **digest -> submit -> sign -> return -> embed** workflow is handled automatically inside the dlib + signtool integration. You don't touch the API at all.

### What signtool + Azure Dlib does for you

When you run:

```powershell
signtool sign /dlib Azure.CodeSigning.Dlib.dll /dmdf metadata.json ...
```

The following happens under the hood:

1. `signtool` computes the file digest (SHA-256, or whatever you specify)
2. `signtool` hands the digest to the Azure Dlib
3. The Dlib calls the Azure Artifact Signing service:
   - Authenticates using your metadata
   - Submits the digest
   - Receives the signed digest
4. `signtool` embeds the signed digest into the PE file:
   - Creates the `WIN_CERTIFICATE` structure
   - Updates the PE headers
   - Handles padding/alignment
   - Handles timestamping
   - Handles verification rules
5. `signtool` returns success/failure

### Why this matters

If you don't use `signtool`, you would need to:

- Compute the SHA digest manually
- Call the Azure REST API
- Receive the signed digest
- Embed the signature into the PE file yourself
- Handle timestamping
- Handle PE checksum updates
- Handle dual-signing
- Handle catalog signing

Using `signtool` avoids all of it.

### Bottom Line

- Your C# method that shells out to `signtool` is all you need
- No digest computation
- No REST API calls
- No PE signature embedding logic

You're using the simplest and most reliable path.