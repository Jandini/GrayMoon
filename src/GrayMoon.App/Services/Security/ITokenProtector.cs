namespace GrayMoon.App.Services.Security;

public interface ITokenProtector
{
    string Protect(string plainText);
    string Unprotect(string protectedValue);
}

