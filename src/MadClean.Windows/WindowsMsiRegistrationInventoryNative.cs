using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using MadClean.Core;

namespace MadClean.Windows;

internal sealed class WindowsMsiRegistrationBudgetException : Exception { }

// Official MsiEnumProductsExW / MsiGetProductInfoExW contracts and current Msi.h verified.
// ProductState's SDK literal is "State". No package, path, SID or action APIs are imported.
internal sealed class WindowsMsiRegistrationInventoryNative(MsiRegistrationScope scope, CancellationToken token)
{
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly List<MsiRegistrationObservation> instances = [];
    private readonly List<string> issues = [];
    private readonly HashSet<MsiRegistrationContext> contexts = [];
    private bool enumerationComplete;

    internal CheckResult<MsiRegistrationReport> Read()
    {
        Check();
        for (uint index = 0; index <= MsiRegistrationReview.MaxInstances; index++)
        {
            Check();
            char[] product = new char[MsiRegistrationReview.ProductCodeBufferCharacters];
            try
            {
                // Both optional SID outputs are NULL: the API does not retrieve any SID value.
                // A non-NULL exact ProductCode prevents an all-products enumeration.
                uint code = MsiEnumProductsExW(scope.ProductCode, null, (uint)scope.ExpectedContextMask, index,
                    product, out uint context, IntPtr.Zero, IntPtr.Zero);
                Check();
                if (code == 259) { enumerationComplete = true; break; }
                if (code != 0)
                { Issue($"Selected-context MSI enumeration did not complete (native code {code}); absence or installer eligibility was not established."); break; }
                if (index == MsiRegistrationReview.MaxInstances)
                { Issue("The bounded MSI instance limit was exceeded; additional contexts are unknown."); break; }
                int end = Array.IndexOf(product, '\0');
                if (end != 38 || !Guid.TryParseExact(new string(product, 0, end), "B", out var parsed) || parsed == Guid.Empty ||
                    !parsed.ToString("B").Equals(scope.ProductCode, StringComparison.OrdinalIgnoreCase) ||
                    context is not (1 or 2 or 4) || (context & (uint)scope.ExpectedContextMask) != context)
                { Issue("The returned product identity/context did not match the exact selected scope; enumeration coverage is unknown."); break; }
                var typedContext = (MsiRegistrationContext)context;
                if (!contexts.Add(typedContext))
                { Issue("A selected MSI context appeared more than once during non-atomic enumeration; coverage is ambiguous."); break; }

                string stateText = Property(context, "State", MsiRegistrationReview.StateCharacters);
                var state = stateText switch
                {
                    "1" => MsiObservedProductState.Advertised,
                    "5" => MsiObservedProductState.Installed,
                    _ => MsiObservedProductState.Unknown
                };
                string name = "Unknown", version = "Unknown", publisher = "Unknown";
                if (state == MsiObservedProductState.Installed)
                {
                    name = Property(context, "InstalledProductName", MsiRegistrationReview.NameCharacters);
                    version = Property(context, "VersionString", MsiRegistrationReview.VersionCharacters);
                    publisher = Property(context, "Publisher", MsiRegistrationReview.PublisherCharacters);
                }
                else if (state == MsiObservedProductState.Advertised)
                {
                    name = Property(context, "ProductName", MsiRegistrationReview.NameCharacters);
                    Issue("This selected context is advertised rather than confirmed installed; installed-only version/publisher properties were not queried and remain Unknown.");
                }
                else Issue("The selected MSI product-state value is unavailable or unsupported; installed/advertised status and installed-only properties remain Unknown.");
                Check();
                instances.Add(new(scope.ProductCode, typedContext, state, name, version, publisher, DateTimeOffset.UtcNow));
            }
            finally { Array.Clear(product); }
        }
        Check();
        return MsiRegistrationReview.Assess(scope, instances.ToArray(), enumerationComplete, issues.ToArray(), DateTimeOffset.UtcNow, token);
    }

    private string Property(uint context, string property, int maximumCharacters)
    {
        // The private caller uses only these exact SDK properties and fixed descriptor capacities.
        int expectedMaximum = property switch
        {
            "State" => MsiRegistrationReview.StateCharacters,
            "ProductName" or "InstalledProductName" => MsiRegistrationReview.NameCharacters,
            "VersionString" => MsiRegistrationReview.VersionCharacters,
            "Publisher" => MsiRegistrationReview.PublisherCharacters,
            _ => throw new ArgumentException("Property is outside the MSI metadata whitelist.", nameof(property))
        };
        if (maximumCharacters != expectedMaximum) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        char[] buffer = new char[maximumCharacters + 1];
        try
        {
            uint count = (uint)buffer.Length;
            Check();
            uint code = MsiGetProductInfoExW(scope.ProductCode, null, context, property, buffer, ref count);
            Check();
            if (code != 0)
            {
                Issue($"MSI {property} query failed or exceeded its fixed bound (native code {code}); the property remains Unknown. No retry, resize or installer action was attempted.");
                return "Unknown";
            }
            if (count > maximumCharacters || buffer[(int)count] != '\0' || Array.IndexOf(buffer, '\0', 0, (int)count) >= 0)
            { Issue($"MSI {property} returned malformed bounded text; the property remains Unknown (native code 0)."); return "Unknown"; }
            // Bounded UTF-16 decoding rejects malformed pairs, controls and format characters;
            // native/provider text is never included in issue messages or activity logs.
            var span = buffer.AsSpan(0, (int)count);
            int position = 0;
            while (position < span.Length)
            {
                Check();
                var status = Rune.DecodeFromUtf16(span[position..], out var rune, out int consumed);
                if (status != System.Buffers.OperationStatus.Done || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format)
                { Issue($"MSI {property} contains unsupported text; the property remains Unknown (native code 0)."); return "Unknown"; }
                position += consumed;
            }
            string result = new(buffer, 0, (int)count);
            if (string.IsNullOrWhiteSpace(result))
            { Issue($"MSI {property} is empty; the property remains Unknown (native code 0)."); return "Unknown"; }
            return result;
        }
        finally { Array.Clear(buffer); }
    }
    private void Check()
    {
        token.ThrowIfCancellationRequested();
        if (watch.Elapsed >= TimeSpan.FromSeconds(20)) throw new WindowsMsiRegistrationBudgetException();
    }
    private void Issue(string message)
    {
        if (issues.Count < MsiRegistrationReview.MaximumIssues) issues.Add(message);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiEnumProductsExW(string productCode, string? userSid, uint contextMask, uint index,
        [Out] char[] installedProductCode, out uint installedContext, IntPtr sid, IntPtr sidCharacters);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiGetProductInfoExW(string productCode, string? userSid, uint context, string property,
        [Out] char[] value, ref uint valueCharacters);
}
