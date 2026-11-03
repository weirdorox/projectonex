using Lumen.Core.Library;
using Microsoft.Win32;

namespace Lumen.Platform.Windows;

/// <summary>Read-only access to the 64-bit registry view.</summary>
public sealed class WindowsRegistryReader : IRegistryReader
{
    public static WindowsRegistryReader Instance { get; } = new();

    private static RegistryKey OpenBase(RegistryRoot root) => RegistryKey.OpenBaseKey(
        root == RegistryRoot.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
        RegistryView.Registry64);

    public string? GetValue(RegistryRoot root, string keyPath, string valueName)
    {
        try
        {
            using var b = OpenBase(root);
            using var k = b.OpenSubKey(keyPath, writable: false);
            var v = k?.GetValue(valueName);
            return v switch
            {
                null => null,
                string s => s,
                int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => v.ToString(),
            };
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, string keyPath)
    {
        try
        {
            using var b = OpenBase(root);
            using var k = b.OpenSubKey(keyPath, writable: false);
            return k?.GetSubKeyNames() ?? [];
        }
        catch
        {
            return [];
        }
    }
}
