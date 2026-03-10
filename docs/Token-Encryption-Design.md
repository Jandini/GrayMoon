### Token encryption design (Level 2)

This document describes how to evolve GrayMoon from **Level 1 token protection** (Base64) to **Level 2 proper encryption** for connector tokens (and any other similar secrets).

The goals:

- **At rest**: Tokens are stored encrypted with a strong, symmetric cipher.
- **In use**: Callers work with a simple abstraction (`ITokenProtector`); most of the app never sees crypto details.
- **Migration-friendly**: Existing Base64-protected tokens can be migrated in place without breaking callers.

---

### Current state (Level 1)

Today the app uses:

- **Storage**:
  - `Connectors.UserToken` stores tokens as **Base64-encoded strings**, applied by:
    - `ConnectorHelpers.ProtectToken`
    - `MigrateConnectorUserTokenBase64Async` (one-time migration for legacy plain-text tokens).
- **Usage**:
  - Services (e.g. `GitHubService`, `NuGetService`, `WorkspaceGitService`) call:
    - `ConnectorHelpers.UnprotectToken` to get a plain-text token when making outbound HTTP requests or sending commands to the agent.

This gives basic obfuscation (no obvious plain text in the DB) but **no cryptographic guarantees**: Base64 is reversible without secrets.

---

### Target state (Level 2)

We want to replace Base64 with **authenticated symmetric encryption** while:

- Keeping the **same `Connectors.UserToken` column** (STRING/TEXT).
- Preserving the same integration points:
  - A **protect** operation before persistence.
  - An **unprotect** operation when using the token.
- Allowing **key rotation** and **versioning**.

High-level approach:

- Introduce an abstraction: `ITokenProtector`.
- Implement an **AES-256-GCM**-based `AesGcmTokenProtector`.
- Have `ConnectorHelpers` delegate to `ITokenProtector` instead of doing Base64 directly.
- Update the migration to:
  - Detect Level 1 (Base64-only) values.
  - Re-encrypt them using AES-GCM and mark them with a scheme/version marker.

---

### Abstraction: `ITokenProtector`

Define a simple interface in `GrayMoon.App` (e.g. under `Services/Security` or `Abstractions`):

```csharp
public interface ITokenProtector
{
    string Protect(string plainText);
    string Unprotect(string protectedValue);
}
```

Key points:

- `Protect` takes a **plain token** and returns an **opaque, persistable string**.
- `Unprotect` takes the persisted string and returns the original token **or throws** if invalid.
- Callers never manipulate keys, IVs, or algorithm details.

We then update dependency injection (e.g. in `Program.cs` / `Startup`) to register a singleton or scoped implementation:

```csharp
services.AddSingleton<ITokenProtector, AesGcmTokenProtector>();
```

---

### AES-GCM implementation (`AesGcmTokenProtector`)

#### Algorithm choice

- **Algorithm**: AES-256-GCM (authenticated encryption with associated data).
- **Key size**: 256 bits (32 bytes).
- **Nonce/IV**: 96 bits (12 bytes) randomly generated per encryption.
- **Tag**: 128 bits (16 bytes) authentication tag.

#### Key management

We keep a single logical key for now, configurable from app settings or environment:

- Environment / config key: `TokenKey` (simple text or Base64).
- Optional `TokenKeyId` when we support rotation.

Behavior:

- When `TokenKey` is **missing or empty**:
  - Use a deterministic built-in default key (for dev / quick start) and log a warning.
- When `TokenKey` is **present**:
  - Try to parse it as Base64. If that succeeds, use the decoded bytes (hashing to 32 bytes with SHA-256 when length != 32).
  - If Base64 parsing fails, treat `TokenKey` as a **plain-text passphrase** and derive the key as `SHA256(UTF8(TokenKey))`.

This makes it easy to use a short password-like value in Docker or appsettings, while still allowing advanced operators to control the key bytes exactly.

#### Serialized format

We want a single string column that:

- Indicates **scheme/version**.
- Contains **IV + ciphertext + tag** in a Base64 payload.

Suggested format:

- `v2:KEYID:BASE64(IV || CIPHERTEXT || TAG)`
  - `v2` – identifies the AES-GCM scheme (Level 2).
  - `KEYID` – optional; allows multiple keys (e.g. `"v1"`, `"2026-03"`). If not used, we can set it to a fixed default such as `"default"`.
  - The final portion is one Base64 string containing **IV (12 bytes) + ciphertext (N) + tag (16 bytes)**.

This string is what we store in `Connectors.UserToken`.

#### Pseudocode

Protect:

```csharp
public string Protect(string plainText)
{
    if (string.IsNullOrWhiteSpace(plainText))
        throw new ArgumentException("Token is required.", nameof(plainText));

    var plaintextBytes = Encoding.UTF8.GetBytes(plainText.Trim());
    var iv = RandomNumberGenerator.GetBytes(12); // GCM recommended IV size
    var ciphertext = new byte[plaintextBytes.Length];
    var tag = new byte[16];

    using var aes = new AesGcm(_key);
    aes.Encrypt(iv, plaintextBytes, ciphertext, tag);

    var combined = new byte[iv.Length + ciphertext.Length + tag.Length];
    Buffer.BlockCopy(iv, 0, combined, 0, iv.Length);
    Buffer.BlockCopy(ciphertext, 0, combined, iv.Length, ciphertext.Length);
    Buffer.BlockCopy(tag, 0, combined, iv.Length + ciphertext.Length, tag.Length);

    var payload = Convert.ToBase64String(combined);
    return $"v2:{_keyId}:{payload}";
}
```

Unprotect:

```csharp
public string Unprotect(string protectedValue)
{
    if (string.IsNullOrWhiteSpace(protectedValue))
        throw new ArgumentException("Value is required.", nameof(protectedValue));

    // Handle legacy Level 1 (Base64-only, no prefix)
    if (!protectedValue.StartsWith("v", StringComparison.Ordinal))
    {
        // Assume Base64 plaintext token
        var bytes = Convert.FromBase64String(protectedValue.Trim());
        return Encoding.UTF8.GetString(bytes);
    }

    var parts = protectedValue.Split(':', 3);
    if (parts.Length != 3 || !string.Equals(parts[0], "v2", StringComparison.Ordinal))
        throw new InvalidOperationException("Unsupported token protection format.");

    var keyId = parts[1]; // For future multi-key support
    var payload = parts[2];

    var combined = Convert.FromBase64String(payload);
    if (combined.Length < 12 + 16)
        throw new InvalidOperationException("Invalid token payload.");

    var iv = combined.AsSpan(0, 12).ToArray();
    var tag = combined.AsSpan(combined.Length - 16, 16).ToArray();
    var ciphertext = combined.AsSpan(12, combined.Length - 12 - 16).ToArray();

    var plaintextBytes = new byte[ciphertext.Length];
    using var aes = new AesGcm(_key);
    aes.Decrypt(iv, ciphertext, tag, plaintextBytes);

    return Encoding.UTF8.GetString(plaintextBytes);
}
```

> Note: The above is illustrative only; production code should include rigorous error handling, logging, and unit tests.

---

### Wiring into `ConnectorHelpers`

Right now, `ConnectorHelpers` implements `ProtectToken`/`UnprotectToken` directly using Base64.

We evolve this by:

- Injecting `ITokenProtector` where needed (e.g. in repositories/services) instead of static helpers, **or**
- Adapting `ConnectorHelpers` to use a static `ITokenProtector` instance resolved from DI at startup (less ideal, but preserves static call sites).

Preferred pattern (non-static usage):

- Refactor code that currently uses:
  - `ConnectorHelpers.ProtectToken`
  - `ConnectorHelpers.UnprotectToken`
- Replace with an injected `ITokenProtector` in relevant services/repositories:
  - `ConnectorRepository`
  - `GitHubService`
  - `NuGetService`
  - `WorkspaceGitService`

Example (constructor injection in `ConnectorRepository`):

```csharp
public sealed class ConnectorRepository(AppDbContext dbContext,
                                        ILogger<ConnectorRepository> logger,
                                        ITokenProtector tokenProtector)
{
    public async Task<Connector> AddAsync(Connector connector)
    {
        connector.UserToken = string.IsNullOrWhiteSpace(connector.UserToken)
            ? null
            : tokenProtector.Protect(connector.UserToken);
        // ...
    }
}
```

And for usage:

```csharp
var bearer = tokenProtector.Unprotect(connector.UserToken);
```

If we cannot inject into all existing call sites easily, we can keep the existing static methods but delegate their logic to the DI-resolved `ITokenProtector`:

- On app startup, capture `ITokenProtector` into a static field that `ConnectorHelpers` uses internally.
- Eventually phase out the static surface in favor of injected dependencies.

---

### Data format compatibility and migration

We need to handle **three formats** during migration:

1. **Legacy plain text** (pre-Level 1) – theoretically no longer present after `MigrateConnectorUserTokenBase64Async`, but we handle it defensively if found.
2. **Level 1 Base64-only** – strings that are valid Base64 but have **no `v2:` prefix**.
3. **Level 2 AES-GCM** – strings starting with `v2:` as defined above.

`ITokenProtector.Unprotect` is responsible for:

- Detecting each format.
- Returning the plain token regardless of underlying encoding/encryption.

#### Migration path

1. **Introduce `ITokenProtector` and `AesGcmTokenProtector`**:
   - Wire into DI.
   - Update `ConnectorRepository` and services to use `ITokenProtector` for all future writes and reads.
   - `Protect` for new writes returns **`v2:...` AES-GCM values**.

2. **Keep `Unprotect` format-aware**:
   - If value starts with `v2:`, decrypt with AES-GCM.
   - Else, treat as Level 1 Base64: `Convert.FromBase64String` and return UTF-8 string.
   - Optionally (for robustness), treat anything non-Base64 as legacy plain text and return as-is.

3. **Background migration of existing values** (optional but recommended):
   - Write a new migration method (e.g. `MigrateConnectorUserTokenEncryptionAsync`) that:
     - Loads connectors with non-null, non-empty `UserToken`.
     - Calls `Unprotect` to get plain token.
     - Calls `Protect` to re-encrypt into `v2:` format.
     - Saves back to DB.
   - This converts existing Base64-only tokens to AES-GCM-encrypted values.

4. **Future cleanup**:
   - After all rows are in `v2:` format and running on AES-GCM for a while, we can:
     - Drop support for legacy Base64-only decoding, or keep it as a safety net.
     - Optionally introduce **new versions** (e.g. `v3`) and support multiple keys.

---

### Key rotation strategy

We can support rotation without changing `Connectors.UserToken` schema:

- Use a **key registry** in memory:

```csharp
public interface ITokenEncryptionKeyProvider
{
    byte[] GetCurrentKey(out string keyId);
    byte[] GetKeyById(string keyId);
}
```

- `AesGcmTokenProtector` calls:
  - `GetCurrentKey(out keyId)` when encrypting.
  - `GetKeyById(keyId)` when decrypting.
- Rotation steps:
  - Add a new key and mark it as current.
  - New encryptions use the new `keyId`.
  - Old rows still decrypt via their stored `keyId`.
  - Optionally, run a background job to re-encrypt old rows with the new key.

---

### Logging and observability

Encryption does not change the **logging policy**:

- Never log raw tokens.
- Never log full encrypted payloads except at **trace level**, and only if required for troubleshooting.
- For correlation / uniqueness checks, consider storing a **separate hash**:
  - E.g. `UserTokenHash = HMACSHA256(TokenHashKey, plainToken)` in a dedicated column.
  - Use this for lookups or “do we already have this token?” checks without storing tokens in plain text.

---

### Summary

- **Abstraction**: Introduce `ITokenProtector` to encapsulate token protection logic.
- **Implementation**: Use AES-256-GCM with a configurable key and `v2:KEYID:BASE64(...)` format.
- **Compatibility**: `Unprotect` supports legacy Base64-only values and new AES-GCM values transparently.
- **Migration**: Add a migration that re-protects existing tokens into `v2:` format; future writes use Level 2 by default.
- **Rotation**: Plan for multiple keys and `keyId` in the serialized format to allow safe key rotation later.

