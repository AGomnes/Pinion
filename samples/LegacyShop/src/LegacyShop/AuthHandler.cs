namespace LegacyShop;

/// <summary>Auth logic — high blast radius in real systems, untested here.</summary>
public class AuthHandler
{
    public bool ValidateToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (token.Length < 16 || token.Length > 256)
            return false;

        bool hasLetter = false, hasDigit = false;
        foreach (char c in token)
        {
            if (char.IsLetter(c)) hasLetter = true;
            else if (char.IsDigit(c)) hasDigit = true;
            else if (c is not ('-' or '_' or '.')) return false;
        }

        return hasLetter && hasDigit && !token.StartsWith("-");
    }

    private string Normalize(string token) => token.Trim().ToLowerInvariant();
}
