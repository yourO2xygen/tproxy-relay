using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace TproxyRelay;

/// <summary>A managed client key: one WEB secret bound to one MTProxy backend listener.</summary>
public sealed record KeyRecord(
    string Id,
    string Name,
    string SecretHex,
    DateTime CreatedUtc,
    DateTime? RevokedUtc,
    bool Paused,
    int BackendPort)
{
    public bool Active => RevokedUtc == null && !Paused;
}

/// <summary>
/// SQLite-backed key registry plus daily per-key traffic aggregates.
/// The database lives on the relay volume; the same volume carries
/// registry.txt for the MTProxy container supervisor.
/// </summary>
public sealed class KeyStore
{
    private readonly string _connectionString;
    private readonly string _dbPath;

    /// <summary>The default MTProxy client port — the built-in env profile.</summary>
    public const int BasePort = 2398;

    public KeyStore(string dbPath)
    {
        _dbPath = dbPath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
        Init();
    }

    public string DataDirectory => Path.GetDirectoryName(Path.GetFullPath(_dbPath))!;

    /// <summary>API-003: schema version. Bump and append a migration step
    /// below; migration is forward-only and idempotent (IF NOT EXISTS).</summary>
    private const long SchemaVersion = 1;

    private void Init()
    {
        using var db = Open();
        long version;
        using (var v = new SqliteCommand("PRAGMA user_version", db))
            version = (long)(v.ExecuteScalar() ?? 0L);
        new SqliteCommand("""
            CREATE TABLE IF NOT EXISTS keys (
              id TEXT PRIMARY KEY,
              name TEXT UNIQUE NOT NULL,
              secret_hex TEXT NOT NULL,
              created_utc TEXT NOT NULL,
              revoked_utc TEXT,
              paused INTEGER NOT NULL DEFAULT 0,
              backend_port INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS traffic_daily (
              day TEXT NOT NULL,
              key_id TEXT NOT NULL,
              up_bytes INTEGER NOT NULL,
              down_bytes INTEGER NOT NULL,
              PRIMARY KEY (day, key_id)
            );
            """, db).ExecuteNonQuery();
        // PERF-006: WAL — concurrent readers never block the reaper's writes.
        new SqliteCommand("PRAGMA journal_mode=WAL", db).ExecuteNonQuery();
        if (version < 1)
        {
            // 001 (API-004/REL-016): parallel Create() calls must never share
            // a backend port. Partial index: revoked rows keep their ports
            // but do not block reuse by a later active key.
            new SqliteCommand(
                "CREATE UNIQUE INDEX IF NOT EXISTS ix_keys_backend_port_active ON keys(backend_port) WHERE revoked_utc IS NULL",
                db).ExecuteNonQuery();
        }
        if (version < SchemaVersion)
            new SqliteCommand($"PRAGMA user_version={SchemaVersion}", db).ExecuteNonQuery();
        RestrictPermissions(_dbPath);
    }

    /// <summary>SEC-003: the key database is a secret registry — owner-only.</summary>
    internal static void RestrictPermissions(string path)
    {
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { /* non-unix filesystems: best effort */ }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // PERF-006: never fail on a brief write lock from the reaper.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA busy_timeout=5000";
            cmd.ExecuteNonQuery();
        }
        return conn;
    }

    // ---- keys ---------------------------------------------------------------

    public IReadOnlyList<KeyRecord> ListKeys(bool includeRevoked = true)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = includeRevoked
            ? "SELECT id,name,secret_hex,created_utc,revoked_utc,paused,backend_port FROM keys ORDER BY created_utc"
            : "SELECT id,name,secret_hex,created_utc,revoked_utc,paused,backend_port FROM keys WHERE revoked_utc IS NULL ORDER BY created_utc";
        using var r = cmd.ExecuteReader();
        var list = new List<KeyRecord>();
        while (r.Read())
            list.Add(Read(r));
        return list;
    }

    public KeyRecord? Get(string id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,name,secret_hex,created_utc,revoked_utc,paused,backend_port FROM keys WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public KeyRecord? GetByName(string name)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,name,secret_hex,created_utc,revoked_utc,paused,backend_port FROM keys WHERE name=$name";
        cmd.Parameters.AddWithValue("$name", name);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public KeyRecord Create(string name, string? secretHex = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
            throw new ArgumentException("key name must be 1..64 characters", nameof(name));
        if (GetByName(name) != null)
            throw new InvalidOperationException($"key name '{name}' already exists");
        var secret = secretHex ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        if (!SecretLooksValid(secret))
            throw new ArgumentException("secret must be 32 hex characters (16 bytes)", nameof(secretHex));
        var id = Guid.NewGuid().ToString("N");
        for (var attempt = 0; ; attempt++)
        {
            var port = AllocatePort();
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO keys (id,name,secret_hex,created_utc,revoked_utc,paused,backend_port)
                VALUES ($id,$name,$secret,$created,NULL,0,$port)
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$secret", secret);
            cmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$port", port);
            try
            {
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException e) when (attempt < 8 &&
                                            e.SqliteErrorCode == 19 &&
                                            e.Message.Contains("backend_port"))
            {
                // Lost the port race to a concurrent Create (API-004): the
                // unique index stopped the duplicate — re-allocate and retry.
                continue;
            }
            return Get(id)!;
        }
    }

    public bool Revoke(string id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE keys SET revoked_utc=$now WHERE id=$id AND revoked_utc IS NULL";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool SetPaused(string id, bool paused)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE keys SET paused=$p WHERE id=$id AND revoked_utc IS NULL";
        cmd.Parameters.AddWithValue("$p", paused ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Lowest backend port not used by any non-revoked key.</summary>
    public int AllocatePort()
    {
        var used = ListKeys().Where(k => k.RevokedUtc == null).Select(k => k.BackendPort).ToHashSet();
        var port = BasePort + 1;
        while (used.Contains(port))
            port++;
        return port;
    }

    public static bool SecretLooksValid(string hex) =>
        hex.Length == 32 && hex.All(char.IsAsciiHexDigit) && hex.Any(c => c != '0');

    private static KeyRecord Read(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        r.GetString(2),
        DateTime.Parse(r.GetString(3)).ToUniversalTime(),
        r.IsDBNull(4) ? null : DateTime.Parse(r.GetString(4)).ToUniversalTime(),
        r.GetInt64(5) != 0,
        r.GetInt32(6));

    // ---- traffic ------------------------------------------------------------

    public void AggregateTraffic(string keyId, long upDelta, long downDelta, DateOnly? day = null) =>
        AggregateTrafficBatch([(keyId, upDelta, downDelta)], day);

    /// <summary>
    /// PERF-006: the reaper flushes every session's deltas in one statement
    /// instead of one INSERT per session.
    /// </summary>
    public void AggregateTrafficBatch(IReadOnlyList<(string KeyId, long Up, long Down)> deltas, DateOnly? day = null)
    {
        if (deltas.Count == 0)
            return;
        var d = (day ?? DateOnly.FromDateTime(DateTime.UtcNow)).ToString("yyyy-MM-dd");
        using var db = Open();
        using var cmd = db.CreateCommand();
        var values = new string[deltas.Count];
        for (var i = 0; i < deltas.Count; i++)
        {
            values[i] = $"($day,$key{i},$up{i},$down{i})";
            cmd.Parameters.AddWithValue($"$key{i}", deltas[i].KeyId);
            cmd.Parameters.AddWithValue($"$up{i}", deltas[i].Up);
            cmd.Parameters.AddWithValue($"$down{i}", deltas[i].Down);
        }
        cmd.CommandText = $"""
            INSERT INTO traffic_daily (day,key_id,up_bytes,down_bytes) VALUES {string.Join(",", values)}
            ON CONFLICT (day,key_id) DO UPDATE SET up_bytes=up_bytes+excluded.up_bytes, down_bytes=down_bytes+excluded.down_bytes
            """;
        cmd.Parameters.AddWithValue("$day", d);
        cmd.ExecuteNonQuery();
    }

    public sealed record TrafficRow(string KeyId, string Day, long UpBytes, long DownBytes);

    public IReadOnlyList<TrafficRow> Traffic(int lastDays = 7, string? keyId = null)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = keyId == null
            ? "SELECT key_id,day,up_bytes,down_bytes FROM traffic_daily WHERE day>=$since ORDER BY day,key_id"
            : "SELECT key_id,day,up_bytes,down_bytes FROM traffic_daily WHERE day>=$since AND key_id=$key ORDER BY day,key_id";
        cmd.Parameters.AddWithValue("$since", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-lastDays)).ToString("yyyy-MM-dd"));
        if (keyId != null)
            cmd.Parameters.AddWithValue("$key", keyId);
        using var r = cmd.ExecuteReader();
        var list = new List<TrafficRow>();
        while (r.Read())
            list.Add(new TrafficRow(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3)));
        return list;
    }

    // ---- registry export (shared with the MTProxy supervisor) ------------------

    /// <summary>
    /// Writes registry.txt next to the database atomically: one
    /// "port:secret_hex" line per active key (the built-in 2398 line comes
    /// from the environment and is not part of this file).
    /// </summary>
    public void ExportRegistry(string builtinSecretHex, int builtinPort = BasePort)
    {
        var lines = new List<string> { $"{builtinPort}:{builtinSecretHex}" };
        foreach (var k in ListKeys(includeRevoked: false).Where(k => k.Active))
            lines.Add($"{k.BackendPort}:{k.SecretHex}");
        var path = Path.Combine(DataDirectory, "registry.txt");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, string.Join('\n', lines) + "\n");
        File.Move(tmp, path, overwrite: true);
        RestrictPermissions(path); // SEC-003: real file mode instead of a marker
    }

    // ---- seed (static operations without the management layer) -----------------

    /// <summary>
    /// Imports /data/keys/seed.json ([{"name":"ivan","secret_hex":"..."}, ...])
    /// idempotently by name. Existing keys are left untouched.
    /// </summary>
    public IReadOnlyList<KeyRecord> ImportSeed(string seedPath)
    {
        if (!File.Exists(seedPath))
            return [];
        var imported = new List<KeyRecord>();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(seedPath));
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var name = el.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || GetByName(name) != null)
                continue;
            var secret = el.TryGetProperty("secret_hex", out var s) ? s.GetString() : null;
            imported.Add(Create(name, string.IsNullOrWhiteSpace(secret) ? null : secret));
        }
        return imported;
    }
}
