namespace GrayMoon.App.Services.Security;

public interface ITokenEncryptionKeyProvider
{
    byte[] GetCurrentKey(out string keyId);
    byte[] GetKeyById(string keyId);
}

