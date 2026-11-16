using Clipt.Services;
using Xunit;

namespace Clipt.Tests;

public class SensitiveDetectorTests
{
    // Secret-shaped inputs are assembled at runtime from obviously fake fragments ("xxxx", "TESTONLY"),
    // so this file contains no complete key, token or credential that a secret scanner (or a person)
    // could mistake for a real one.
    private static string Fake(params string[] parts) => string.Concat(parts);
    private static string Filler(int length) => new('x', length);

    public static TheoryData<string, string> Secrets() => new()
    {
        { "Tr0ub4dor&3x!", "Looks like a password" },
        { "aZ3kP9qLx7Wm", "Looks like a password" },
        { "Hunter2!xyz", "Looks like a password" },
        { Fake("sk-", "ant-", "api03-", Filler(40)), "API key or token" },
        { Fake("AK", "IA", "TESTONLY", "00000000"), "API key or token" },
        { Fake("gh", "p_", Filler(36)), "API key or token" },
        { Fake("xo", "xb-", "000000000000-", "testonlytoken"), "API key or token" },
        { Fake("ey", "J", "hbGciOiJub25lIn0", ".", "ey", "J", "zdWIiOiJ0ZXN0In0", ".", "dGVzdHNpZ25hdHVyZQ"), "Access token" },
        { Fake("-----BEGIN OPENSSH ", "PRIVATE KEY-----\n", "AAAA\n", "-----END OPENSSH ", "PRIVATE KEY-----"), "Private key" },
        { "4111 1111 1111 1111", "Card number" },          // Visa's published test number
        { "5500-0000-0000-0004", "Card number" },          // Mastercard's published test number
        { "GB82 WEST 1234 5698 7654 32", "Bank account number" }, // ISO 13616 example IBAN
        { "DE89370400440532013000", "Bank account number" },       // ISO 13616 example IBAN
        { Fake("postgres://", "user", ":", "not-a-real-pw", "@", "db.example.com/app"), "Password in a link" },
        { Fake("Server=db;User Id=sa;", "Pass", "word", "=", "Not-A-Real-Pw1;"), "Contains a password" },
        { Fake("export API_", "KEY=", "testonly1234"), "Contains a password" },
    };

    [Theory]
    [MemberData(nameof(Secrets))]
    public void Detects_secrets(string text, string expected) =>
        Assert.Equal(expected, SensitiveDetector.Detect(text));

    [Theory]
    [InlineData("Remember to buy coffee beans")]
    [InlineData("getUserById2")]
    [InlineData("my_variable_1")]
    [InlineData("Item_Count-2")]
    [InlineData("https://github.com/anthropics/anthropic-sdk-csharp")]
    [InlineData("john.doe@example.com")]
    [InlineData("C:\\Users\\Public\\Documents\\Report2024.docx")]
    [InlineData("#7C5CFF")]
    [InlineData("v10.0.5-preview.1")]
    [InlineData("3f2a9c1e7b4d8e6f0a1b2c3d4e5f6a7b8c9d0e1f")] // git SHA
    [InlineData("123e4567-e89b-12d3-a456-426614174000")]     // UUID
    [InlineData("4111 1111 1111 1112")]                       // fails Luhn
    [InlineData("+39 333 123 4567")]                          // phone number
    [InlineData("public static int Add(int a, int b)")]
    [InlineData("HelloWorld")]
    [InlineData("The password policy changed last week.")]
    public void Ignores_ordinary_text(string text) =>
        Assert.Null(SensitiveDetector.Detect(text));

    [Fact]
    public void Private_browser_titles_are_recognised()
    {
        Assert.True(ClipboardMonitor.IsPrivateBrowserTitle("New tab - [InPrivate] - Microsoft Edge"));
        Assert.True(ClipboardMonitor.IsPrivateBrowserTitle("Example — Mozilla Firefox Private Browsing"));
        Assert.True(ClipboardMonitor.IsPrivateBrowserTitle("New Tab - Brave (Private)"));
        Assert.False(ClipboardMonitor.IsPrivateBrowserTitle("Inbox - Outlook"));
    }
}
