using System.Collections.Concurrent;
using Lumen.Core.Diagnostics;
using Lumen.Core.Library;

namespace Lumen.Data;

/// <summary>Write-through cached key/value settings stored in the <c>settings</c> table.</summary>
public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly LumenDatabase _db;
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SqliteSettingsStore(LumenDatabase db)
    {
        _db = db;
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM settings";
        using var r = cmd.ExecuteReader();
        while (r.Read()) _cache[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1);
    }

    public string? Get(string key) => _cache.TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string? value)
    {
        _cache[key] = value;
        try
        {
            using var c = _db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO settings(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to persist setting '{key}'", ex);
        }
    }
}
