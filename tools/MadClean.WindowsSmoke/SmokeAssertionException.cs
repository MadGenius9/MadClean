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
    public static string SafeContext(Exception exception)
    {
        var contexts = new List<string>();
        Exception? current = exception;
        for (int depth = 0; current != null && depth < 3; depth++, current = current.InnerException)
        {
            string context = SafeType(current);
            var trace = new System.Diagnostics.StackTrace(current, true);
            foreach (var frame in trace.GetFrames())
            {
                var method = frame.GetMethod();
                var type = method?.DeclaringType;
                if (type == null || !IsApplicationAssembly(type.Assembly)) continue;
                while (type.DeclaringType != null) type = type.DeclaringType;
                int line = frame.GetFileLineNumber();
                context += " at " + SafeMetadata(type.Name) + "." + SafeMetadata(method!.Name) + (line > 0 ? ":" + line.ToString(System.Globalization.CultureInfo.InvariantCulture) : ":unknown-line");
                break;
            }
            contexts.Add(context);
        }
        return string.Join(" <- ", contexts);
    }

    private static bool IsApplicationAssembly(System.Reflection.Assembly assembly) =>
        assembly == typeof(Program).Assembly || assembly == typeof(MadClean.App.MainWindow).Assembly ||
        assembly == typeof(MadClean.Core.CheckState).Assembly || assembly == typeof(MadClean.Windows.WindowsDiagnosticsInventory).Assembly;

    private static string SafeMetadata(string name) => new(name.Where(character => char.IsAsciiLetterOrDigit(character) || character == '_').Take(80).ToArray());

    public static string SafeType(Exception exception)
    {
        string name = exception.GetType().Name;
        return name.Length is > 0 and <= 96 && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '`')
            ? name : "Exception";
    }
}
