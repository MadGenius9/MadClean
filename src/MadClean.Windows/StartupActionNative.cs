using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MadClean.Core;
using Microsoft.Win32.SafeHandles;

namespace MadClean.Windows;

internal sealed class StartupActionNativeFailure(string operation, int code) : IOException($"{operation} failed (native code {code}).")
{
    internal int NativeCode { get; } = code;
}

internal sealed class StartupActionConflict : IOException
{
    internal StartupActionConflict() : base("The selected registration or recovery record changed; re-preview is required.") { }
}

internal sealed class StartupActionRawValue(uint kind, byte[] data) : IDisposable
{
    internal uint Kind { get; } = kind;
    internal byte[] Data { get; } = data;
    public void Dispose() => CryptographicOperations.ZeroMemory(Data);
}

internal static class StartupActionNative
{
    internal const uint QueryValue = 1;
    internal const uint EnumerateSubKeys = 8;
    private const int Missing = 2;

    internal sealed class RegistryLease : IDisposable
    {
        internal List<SafeRegistryHandle> Handles { get; } = [];
        internal string[] MissingComponents { get; set; } = [];
        internal SafeRegistryHandle Leaf => Handles[^1];
        public void Dispose() { for (int i = Handles.Count - 1; i >= 0; i--) Handles[i].Dispose(); }
    }

    internal static RegistryLease OpenRead(string path, bool allowMissing = false)
    {
        // Check only the current token's own loaded hive; never enumerate or select another user.
        string sid = StartupActionPayloadCodec.CurrentSid();
        int loadedCode = RegOpenKeyExW(new IntPtr(unchecked((int)0x80000003)), sid, 8, QueryValue, out var loaded);
        if (loadedCode != 0) { loaded.Dispose(); throw new StartupActionNativeFailure("Loaded current-user profile validation", loadedCode); }
        using (loaded) RejectLink(loaded);
        int code = RegOpenCurrentUser(QueryValue | EnumerateSubKeys, out var root);
        if (code != 0) { root.Dispose(); throw new StartupActionNativeFailure("Current-user registry anchor open", code); }
        var lease = new RegistryLease(); lease.Handles.Add(root);
        try
        {
            RejectLink(root);
            var parts = path.Split('\\');
            if (parts.Length == 0 || parts.Any(part => string.IsNullOrEmpty(part) || part is "." or ".."))
                throw new NotSupportedException("Unsupported registry path components.");
            for (int i = 0; i < parts.Length; i++)
            {
                code = RegOpenRelativeKeyExW(lease.Leaf, parts[i], 8, QueryValue | EnumerateSubKeys, out var child);
                if (code == 2 && allowMissing)
                {
                    child.Dispose(); lease.MissingComponents = parts[i..]; return lease;
                }
                if (code != 0 || child.IsInvalid) { child.Dispose(); throw new StartupActionNativeFailure("No-follow current-user registry component open", code); }
                lease.Handles.Add(child); RejectLink(child);
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static void RejectLink(SafeRegistryHandle key)
    {
        uint size = 0;
        int code = RegQueryTypeW(key, "SymbolicLinkValue", IntPtr.Zero, out uint kind, IntPtr.Zero, ref size);
        if ((code == 0 || code == 234) && kind == 6)
            throw new NotSupportedException("Registry symbolic-link components are not permitted.");
        if (code != 0 && code != 2 && code != 234) throw new StartupActionNativeFailure("Registry-link metadata validation", code);
    }

    internal static StartupActionRawValue? ReadSource(SafeRegistryHandle key, string name)
    {
        var buffer = new byte[StartupActionPolicy.MaximumValueBytes];
        try
        {
            uint count = (uint)buffer.Length;
            int code = RegQueryValueExW(key, name, IntPtr.Zero, out uint kind, buffer, ref count);
            if (code == Missing) return null;
            if (code == 234 || count > buffer.Length) throw new NotSupportedException("The selected registration exceeds the supported command-byte bound.");
            if (code != 0) throw new StartupActionNativeFailure("Private selected-value read", code);
            if (!StartupActionPolicy.IsSupportedRawValue(kind, buffer.AsSpan(0, checked((int)count))))
                throw new NotSupportedException("The selected value is not bounded NUL-terminated REG_SZ/REG_EXPAND_SZ UTF-16 metadata.");
            return new(kind, buffer.AsSpan(0, checked((int)count)).ToArray());
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    internal static IEnumerable<string> Names(SafeRegistryHandle key, bool subkeys, int maximum, CancellationToken token)
    {
        for (uint index = 0; index <= maximum; index++)
        {
            token.ThrowIfCancellationRequested();
            var name = new System.Text.StringBuilder(StartupActionPolicy.MaximumValueNameCharacters + 1);
            uint length = (uint)name.Capacity;
            int code = subkeys
                ? RegEnumKeyExW(key, index, name, ref length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)
                : RegEnumValueW(key, index, name, ref length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (code == 259) yield break;
            if (code != 0) throw new StartupActionNativeFailure("Bounded registry-name enumeration", code);
            if (index == maximum) throw new NotSupportedException("Registry-name enumeration reached its coverage bound.");
            yield return name.ToString();
        }
    }

    internal static byte[] ReadJournal(SafeRegistryHandle key)
    {
        var buffer = new byte[StartupActionPolicy.MaximumPrivatePayloadBytes];
        try
        {
            uint count = (uint)buffer.Length;
            int code = RegQueryValueExW(key, "Record", IntPtr.Zero, out uint kind, buffer, ref count);
            if (code != 0) throw new StartupActionNativeFailure("Encrypted recovery-record read", code);
            if (kind != 3 || count == 0 || count > buffer.Length) throw new InvalidDataException("Encrypted recovery-record type/length is unsupported.");
            return buffer.AsSpan(0, checked((int)count)).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegOpenKeyExW(IntPtr root, string path, uint options, uint rights, out SafeRegistryHandle key);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "RegOpenKeyExW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegOpenRelativeKeyExW(SafeRegistryHandle root, string path, uint options, uint rights, out SafeRegistryHandle key);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegOpenCurrentUser(uint rights, out SafeRegistryHandle key);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "RegQueryValueExW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegQueryTypeW(SafeRegistryHandle key, string name, IntPtr reserved, out uint kind, IntPtr data, ref uint bytes);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegEnumKeyExW(SafeRegistryHandle key, uint index, System.Text.StringBuilder name, ref uint length,
        IntPtr reserved, IntPtr keyClass, IntPtr classLength, IntPtr lastWrite);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegEnumValueW(SafeRegistryHandle key, uint index, System.Text.StringBuilder name, ref uint length,
        IntPtr reserved, IntPtr kind, IntPtr data, IntPtr bytes);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegQueryValueExW(SafeRegistryHandle key, string name, IntPtr reserved,
        out uint kind, [Out] byte[] data, ref uint bytes);

}

internal static class StartupActionDataProtection
{
    private static readonly byte[] Entropy = System.Text.Encoding.ASCII.GetBytes("MadClean.StartupRegistration.v1");

    internal static byte[] Unprotect(byte[] data)
    {
        if (data.Length == 0 || data.Length > StartupActionPolicy.MaximumPrivatePayloadBytes)
            throw new InvalidDataException("Private payload size is unsupported.");
        DataBlob input = default, entropy = default, output = default;
        try
        {
            input = Allocate(data); entropy = Allocate(Entropy);
            bool success = CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, StartupActionPolicy.DataProtectionFlags, out output);
            if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
            if (output.Bytes == 0 || output.Bytes > StartupActionPolicy.MaximumPrivatePayloadBytes)
                throw new InvalidDataException("Protected payload size is unsupported.");
            var result = new byte[checked((int)output.Bytes)];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero) { Zero(input); Marshal.FreeHGlobal(input.Data); }
            if (entropy.Data != IntPtr.Zero) { Zero(entropy); Marshal.FreeHGlobal(entropy.Data); }
            if (output.Data != IntPtr.Zero) { Zero(output); LocalFree(output.Data); }
        }
    }
    private static DataBlob Allocate(byte[] data)
    {
        IntPtr pointer = Marshal.AllocHGlobal(data.Length); Marshal.Copy(data, 0, pointer, data.Length);
        return new() { Bytes = (uint)data.Length, Data = pointer };
    }
    private static void Zero(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero && blob.Bytes <= StartupActionPolicy.MaximumPrivatePayloadBytes)
            Marshal.Copy(new byte[checked((int)blob.Bytes)], 0, blob.Data, checked((int)blob.Bytes));
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { internal uint Bytes; internal IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
