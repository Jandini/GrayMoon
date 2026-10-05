namespace GrayMoon.App.Services.Security;

public interface ITokenEncryptionKeyProvider
{
    byte[] GetCurrentKey(out string keyId);
    byte[] GetKeyById(string keyId);

    /// <summary>True when <paramref name="keyId"/> identifies the old built-in, source-derived key that
    /// every install used before a per-install key file existed. Used only to decide which stored tokens
    /// need re-encrypting on startup; it is never used to encrypt new values.</summary>
    bool IsLegacyKeyId(string keyId);
}
