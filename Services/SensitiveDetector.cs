using System.Text.RegularExpressions;

namespace Clipt.Services;

/// <summary>
/// Heuristics that recognise secrets people commonly copy: passwords, API keys and tokens, private keys,
/// payment card numbers, bank account numbers and credentials embedded in URLs or connection strings.
/// Returns a short human-readable reason, or null if nothing sensitive was found.
/// </summary>
public static partial class SensitiveDetector
{
    public static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();

        if (PrivateKey().IsMatch(t)) return "Private key";
        if (KnownToken().IsMatch(t)) return "API key or token";
        if (Jwt().IsMatch(t)) return "Access token";
        if (UrlCredentials().IsMatch(t)) return "Password in a link";
        if (PasswordAssignment().IsMatch(t)) return "Contains a password";

        // The remaining checks only apply to short, single-value copies.
        if (t.Length > 80 || t.Contains('\n')) return null;

        if (LooksLikeCardNumber(t)) return "Card number";
        if (LooksLikeIban(t)) return "Bank account number";
        if (LooksLikePassword(t)) return "Looks like a password";
        return null;
    }

    private static bool LooksLikeCardNumber(string t)
    {
        if (!CardShape().IsMatch(t)) return false;
        var digits = new string(t.Where(char.IsDigit).ToArray());
        if (digits.Length is < 13 or > 19 || digits[0] is not ('2' or '3' or '4' or '5' or '6')) return false;
        return Luhn(digits);
    }

    private static bool Luhn(string digits)
    {
        int sum = 0;
        bool dbl = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (dbl) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            dbl = !dbl;
        }
        return sum % 10 == 0;
    }

    private static bool LooksLikeIban(string t)
    {
        var s = t.Replace(" ", "").ToUpperInvariant();
        if (!IbanShape().IsMatch(s)) return false;
        // ISO 13616 mod-97 check.
        var rearranged = s[4..] + s[..4];
        int rem = 0;
        foreach (char c in rearranged)
        {
            var chunk = char.IsLetter(c) ? (c - 'A' + 10).ToString() : c.ToString();
            foreach (char d in chunk) rem = (rem * 10 + (d - '0')) % 97;
        }
        return rem == 1;
    }

    private static bool LooksLikePassword(string t)
    {
        if (t.Length is < 8 or > 64) return false;
        if (t.Any(char.IsWhiteSpace)) return false;
        if (t.Contains("://") || t.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return false;
        if (Email().IsMatch(t) || PathLike().IsMatch(t) || HexOrUuid().IsMatch(t) || Version().IsMatch(t)) return false;
        if (t.StartsWith('#') || t.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)) return false;

        // '_', '-' and '.' are common in identifiers and file names, so they don't count as "symbols".
        bool strongSymbol = t.Any(c => !char.IsLetterOrDigit(c) && c is not ('_' or '-' or '.'));
        int classes = (strongSymbol ? 1 : 0);
        if (t.Any(char.IsLower)) classes++;
        if (t.Any(char.IsUpper)) classes++;
        if (t.Any(char.IsDigit)) classes++;
        if (classes < 3) return false;

        // Word-shaped identifiers (getUserById2, Item_Count-2) aren't passwords; random strings (aZ3kP9qL) are.
        if (!strongSymbol && WordShaped().IsMatch(t.Replace("_", "").Replace("-", "").Replace(".", ""))) return false;
        return Entropy(t) >= 2.8;
    }

    private static double Entropy(string s)
    {
        var counts = s.GroupBy(c => c).Select(g => (double)g.Count() / s.Length);
        return -counts.Sum(p => p * Math.Log2(p));
    }

    [GeneratedRegex(@"-----BEGIN ((RSA|EC|DSA|OPENSSH|ENCRYPTED|PGP) )?PRIVATE KEY( BLOCK)?-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(
        @"(sk-ant-[A-Za-z0-9_\-]{20,}" +           // Anthropic
        @"|sk-(proj-|svcacct-)?[A-Za-z0-9_\-]{20,}" + // OpenAI-style
        @"|(AKIA|ASIA)[0-9A-Z]{16}" +               // AWS access key id
        @"|gh[pousr]_[A-Za-z0-9]{36,}" +            // GitHub tokens
        @"|github_pat_[A-Za-z0-9_]{22,}" +
        @"|glpat-[A-Za-z0-9_\-]{20,}" +             // GitLab
        @"|xox[abposr]-[A-Za-z0-9\-]{10,}" +        // Slack
        @"|AIza[0-9A-Za-z_\-]{35}" +                // Google API key
        @"|(sk|rk)_live_[0-9A-Za-z]{20,}" +         // Stripe
        @"|npm_[A-Za-z0-9]{36}" +                   // npm
        @"|SG\.[A-Za-z0-9_\-]{16,}\.[A-Za-z0-9_\-]{16,}" + // SendGrid
        @"|AccountKey=[A-Za-z0-9+/=]{40,}" +        // Azure storage
        @"|SharedAccessSignature=|sig=[A-Za-z0-9%+/=]{20,})")]
    private static partial Regex KnownToken();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b[a-z][a-z0-9+.\-]*://[^/\s:@]+:[^/\s@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"\b(password|passwd|pwd|passphrase|secret|api[_-]?key|client[_-]?secret|access[_-]?token)\s*[:=]\s*[""']?[^\s""';]{4,}", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordAssignment();

    [GeneratedRegex(@"^[0-9][0-9 \-]{11,22}[0-9]$")]
    private static partial Regex CardShape();

    [GeneratedRegex(@"^[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}$")]
    private static partial Regex IbanShape();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[a-z]{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex Email();

    [GeneratedRegex(@"^([a-zA-Z]:\\|\\\\|/|~/|\./)")]
    private static partial Regex PathLike();

    [GeneratedRegex(@"^([0-9a-fA-F]+|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$")]
    private static partial Regex HexOrUuid();

    [GeneratedRegex(@"^v?\d+(\.\d+){1,3}([\-+][0-9A-Za-z.\-]+)?$")]
    private static partial Regex Version();

    // Sequences of words: lowercase runs, optionally capitalised, with digits only between/after words.
    [GeneratedRegex(@"^([A-Z]?[a-z]+|[A-Z]{2,}(?=[A-Z][a-z]|\d|$)|\d+)+$")]
    private static partial Regex WordShaped();
}
