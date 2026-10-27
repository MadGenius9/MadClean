namespace MadClean.WindowsSmoke;

/// <summary>Only fixed harness-authored invariant codes may be included in diagnostic evidence.</summary>
internal sealed class SmokeAssertionException : Exception
{
    public SmokeAssertionException(string code) : base("A fixed Windows smoke invariant failed.")
    {
        Code = code.Length is > 0 and <= 80 && code.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            ? code : "invalid-invariant-code";
    }

    public string Code { get; }
    public static string SafeType(Exception exception)
    {
        string name = exception.GetType().Name;
        return name.Length is > 0 and <= 96 && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '`')
            ? name : "Exception";
    }
}
