namespace GrayMoon.App.Services.Security;

public interface ITokenProtector
{
    string Protect(string plainText);
    string Unprotect(string protectedValue);

    /// <summary>Returns the key id a value was protected with, or null when it is not in the current
    /// versioned scheme (for example legacy plain text/Base64). Used only to decide whether a stored value
    /// needs re-encrypting with the current key; never required for Unprotect to work.</summary>
    string? TryGetKeyId(string protectedValue);
}

