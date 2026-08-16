using System.Runtime.InteropServices;
using System.Text;

namespace LearnCards.Web.Data;

/// <summary>
/// SQLite-Provider über direktes P/Invoke auf die System-SQLite-Bibliothek —
/// ohne NuGet-Abhängigkeit. Unterstützt Linux (libsqlite3), Windows (winsqlite3.dll,
/// seit Windows 10 Bestandteil des Systems) und macOS (libsqlite3.dylib).
/// </summary>
public sealed class SqliteDatabase : IDatabase
{
    private readonly string _path;
    private IntPtr _db;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string ProviderName => "sqlite";

    public SqliteDatabase(string path)
    {
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public async Task InitAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Check(Native.sqlite3_open_v2(Utf8(_path), out _db,
                Native.SQLITE_OPEN_READWRITE | Native.SQLITE_OPEN_CREATE | Native.SQLITE_OPEN_FULLMUTEX, IntPtr.Zero));
            ExecRaw("PRAGMA journal_mode=WAL");
            ExecRaw("PRAGMA foreign_keys=ON");
            ExecRaw("PRAGMA busy_timeout=5000");
            foreach (var ddl in Schema.Statements(ProviderName)) ExecRaw(ddl);
            EnsureColumn("cards", "reference_answer", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn("cards", "official_sources_json", "TEXT NOT NULL DEFAULT '[]'");
            EnsureColumn("cards", "slide_number", "INTEGER NULL");
            EnsureColumn("cards", "target_time_sec", "INTEGER NULL");
            EnsureColumn("cards", "quiz_json", "TEXT NOT NULL DEFAULT '[]'");
            EnsureColumn("quiz_results", "card_id", "TEXT NULL");
            EnsureColumn("quiz_results", "stats_json", "TEXT NOT NULL DEFAULT '{}'");
        }
        finally { _gate.Release(); }
    }

    public async Task<List<Dictionary<string, string?>>> QueryAsync(string sql, IReadOnlyList<(string, object?)>? args = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return Run(sql, args, wantRows: true).Rows; }
        finally { _gate.Release(); }
    }

    public async Task<int> ExecuteAsync(string sql, IReadOnlyList<(string, object?)>? args = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { Run(sql, args, wantRows: false); return Native.sqlite3_changes(_db); }
        finally { _gate.Release(); }
    }

    private (List<Dictionary<string, string?>> Rows, int _) Run(string sql, IReadOnlyList<(string, object?)>? args, bool wantRows)
    {
        var rows = new List<Dictionary<string, string?>>();
        Check(Native.sqlite3_prepare_v2(_db, Utf8(sql), -1, out var stmt, IntPtr.Zero));
        try
        {
            if (args is not null)
                foreach (var (name, value) in args)
                {
                    var idx = Native.sqlite3_bind_parameter_index(stmt, Utf8("@" + name));
                    if (idx == 0) continue; // Parameter im SQL nicht verwendet
                    var text = DbValue.ToText(value);
                    if (text is null) Check(Native.sqlite3_bind_null(stmt, idx));
                    else
                    {
                        var bytes = Encoding.UTF8.GetBytes(text);
                        Check(Native.sqlite3_bind_text(stmt, idx, bytes, bytes.Length, Native.SQLITE_TRANSIENT));
                    }
                }

            int rc;
            while ((rc = Native.sqlite3_step(stmt)) == Native.SQLITE_ROW)
            {
                if (!wantRows) continue;
                var n = Native.sqlite3_column_count(stmt);
                var row = new Dictionary<string, string?>(n, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < n; i++)
                {
                    var name = FromUtf8(Native.sqlite3_column_name(stmt, i)) ?? $"col{i}";
                    row[name] = Native.sqlite3_column_type(stmt, i) == Native.SQLITE_NULL
                        ? null
                        : FromUtf8(Native.sqlite3_column_text(stmt, i));
                }
                rows.Add(row);
            }
            if (rc != Native.SQLITE_DONE) Check(rc);
            return (rows, 0);
        }
        finally { Native.sqlite3_finalize(stmt); }
    }

    private void ExecRaw(string sql) => Run(sql, null, wantRows: false);

    private void EnsureColumn(string table, string column, string definition)
    {
        var rows = Run($"PRAGMA table_info({table})", null, wantRows: true).Rows;
        var exists = rows.Any(r => string.Equals(r.GetValueOrDefault("name"), column, StringComparison.OrdinalIgnoreCase));
        if (!exists) ExecRaw($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    private void Check(int rc)
    {
        if (rc is Native.SQLITE_OK or Native.SQLITE_ROW or Native.SQLITE_DONE) return;
        var msg = _db != IntPtr.Zero ? FromUtf8(Native.sqlite3_errmsg(_db)) : null;
        throw new InvalidOperationException($"SQLite-Fehler {rc}: {msg ?? "unbekannt"}");
    }

    private static byte[] Utf8(string s)
    {
        var b = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0);
        return b; // nullterminiert
    }

    private static string? FromUtf8(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        var len = 0;
        while (Marshal.ReadByte(p, len) != 0) len++;
        var buf = new byte[len];
        Marshal.Copy(p, buf, 0, len);
        return Encoding.UTF8.GetString(buf);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_db != IntPtr.Zero) { Native.sqlite3_close_v2(_db); _db = IntPtr.Zero; }
        }
        finally { _gate.Release(); }
    }

    // ─── P/Invoke ───────────────────────────────────────────────────────────
    private static class Native
    {
        public const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101, SQLITE_NULL = 5;
        public const int SQLITE_OPEN_READWRITE = 0x2, SQLITE_OPEN_CREATE = 0x4, SQLITE_OPEN_FULLMUTEX = 0x10000;
        public static readonly IntPtr SQLITE_TRANSIENT = new(-1);

        private const string Lib = "sqlite3";

        static Native()
        {
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, asm, path) =>
            {
                if (name != Lib) return IntPtr.Zero;
                string[] candidates = OperatingSystem.IsWindows()
                    ? new[] { "sqlite3.dll", "winsqlite3.dll", "e_sqlite3.dll" }
                    : OperatingSystem.IsMacOS()
                        ? new[] { "libsqlite3.dylib", "/usr/lib/libsqlite3.dylib" }
                        : new[] { "libsqlite3.so.0", "libsqlite3.so", "/usr/lib/x86_64-linux-gnu/libsqlite3.so.0" };
                foreach (var c in candidates)
                    if (NativeLibrary.TryLoad(c, out var h)) return h;
                return IntPtr.Zero;
            });
        }

        [DllImport(Lib)] public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Lib)] public static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Lib)] public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nBytes, out IntPtr stmt, IntPtr tail);
        [DllImport(Lib)] public static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Lib)] public static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Lib)] public static extern int sqlite3_changes(IntPtr db);
        [DllImport(Lib)] public static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport(Lib)] public static extern int sqlite3_bind_parameter_index(IntPtr stmt, byte[] name);
        [DllImport(Lib)] public static extern int sqlite3_bind_text(IntPtr stmt, int idx, byte[] value, int nBytes, IntPtr destructor);
        [DllImport(Lib)] public static extern int sqlite3_bind_null(IntPtr stmt, int idx);
        [DllImport(Lib)] public static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport(Lib)] public static extern IntPtr sqlite3_column_name(IntPtr stmt, int col);
        [DllImport(Lib)] public static extern int sqlite3_column_type(IntPtr stmt, int col);
        [DllImport(Lib)] public static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    }
}
