using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Data;

/// <summary>
/// Minimaler PostgreSQL-Client direkt auf dem Wire-Protokoll (v3) — ohne NuGet-Abhängigkeit.
/// Unterstützt SCRAM-SHA-256, MD5- und Cleartext-Authentifizierung sowie TLS
/// (sslmode=disable|prefer|require; require ist für Azure Database for PostgreSQL nötig).
/// Alle Abfragen laufen über das Extended-Query-Protokoll mit gebundenen Parametern.
/// </summary>
public sealed class PostgresDatabase : IDatabase
{
    private readonly PostgresSettings _s;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _tcp;
    private Stream? _stream;

    public string ProviderName => "postgres";

    public PostgresDatabase(PostgresSettings settings) => _s = settings;

    public async Task InitAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(ct);
            foreach (var ddl in Schema.Statements(ProviderName))
                await ExecCoreAsync(ddl, null, ct);
            await MigrateLegacySchemaAsync(ct);
            await MigrateCardContentColumnsAsync(ct);
            await MigrateQuizResultColumnsAsync(ct);
            await MigrateModuleDeleteCascadeAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<List<Dictionary<string, string?>>> QueryAsync(string sql, IReadOnlyList<(string, object?)>? args = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return (await RunWithRetryAsync(sql, args, ct)).Rows; }
        finally { _gate.Release(); }
    }

    public async Task<int> ExecuteAsync(string sql, IReadOnlyList<(string, object?)>? args = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return (await RunWithRetryAsync(sql, args, ct)).Affected; }
        finally { _gate.Release(); }
    }

    private async Task<(List<Dictionary<string, string?>> Rows, int Affected)> RunWithRetryAsync(
        string sql, IReadOnlyList<(string, object?)>? args, CancellationToken ct)
    {
        try
        {
            await EnsureConnectedAsync(ct);
            return await ExecCoreAsync(sql, args, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // Verbindung verloren → einmal neu verbinden und wiederholen
            CloseSilently();
            await EnsureConnectedAsync(ct);
            return await ExecCoreAsync(sql, args, ct);
        }
    }

    private async Task MigrateLegacySchemaAsync(CancellationToken ct)
    {
        var rows = await ExecCoreAsync("""
            SELECT data_type
            FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name = 'cards'
              AND column_name = 'archived'
            """, null, ct);
        var dataType = rows.Rows.FirstOrDefault()?.GetValueOrDefault("data_type");
        if (!string.Equals(dataType, "boolean", StringComparison.OrdinalIgnoreCase)) return;

        await ExecCoreAsync("ALTER TABLE cards ALTER COLUMN archived DROP DEFAULT", null, ct);
        await ExecCoreAsync("""
            ALTER TABLE cards
            ALTER COLUMN archived TYPE INTEGER
            USING CASE WHEN archived THEN 1 ELSE 0 END
            """, null, ct);
        await ExecCoreAsync("ALTER TABLE cards ALTER COLUMN archived SET DEFAULT 0", null, ct);
    }

    private async Task MigrateModuleDeleteCascadeAsync(CancellationToken ct)
    {
        await EnsureCascadeConstraintAsync("cards", "module_id", "modules", "id", "cards_module_id_fkey", ct);
        await EnsureCascadeConstraintAsync("quiz_results", "module_id", "modules", "id", "quiz_results_module_id_fkey", ct);
    }

    private async Task MigrateCardContentColumnsAsync(CancellationToken ct)
    {
        await EnsureCardColumnAsync("reference_answer", "TEXT NOT NULL DEFAULT ''", ct);
        await EnsureCardColumnAsync("official_sources_json", "TEXT NOT NULL DEFAULT '[]'", ct);
        await EnsureCardColumnAsync("slide_number", "INTEGER NULL", ct);
        await EnsureCardColumnAsync("target_time_sec", "INTEGER NULL", ct);
        await EnsureCardColumnAsync("quiz_json", "TEXT NOT NULL DEFAULT '[]'", ct);
    }

    private async Task MigrateQuizResultColumnsAsync(CancellationToken ct)
    {
        await EnsureTableColumnAsync("quiz_results", "card_id", "UUID NULL", ct);
        await EnsureTableColumnAsync("quiz_results", "stats_json", "TEXT NOT NULL DEFAULT '{}'", ct);
    }

    private async Task EnsureCardColumnAsync(string columnName, string definition, CancellationToken ct)
        => await EnsureTableColumnAsync("cards", columnName, definition, ct);

    private async Task EnsureTableColumnAsync(string tableName, string columnName, string definition, CancellationToken ct)
    {
        var rows = await ExecCoreAsync("""
            SELECT 1
            FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name = @table_name
              AND column_name = @column_name
            """, new (string, object?)[]
        {
            ("table_name", tableName),
            ("column_name", columnName),
        }, ct);
        if (rows.Rows.Count == 0)
            await ExecCoreAsync($"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition}", null, ct);
    }

    private async Task EnsureCascadeConstraintAsync(
        string tableName,
        string columnName,
        string referencedTable,
        string referencedColumn,
        string desiredConstraintName,
        CancellationToken ct)
    {
        var rows = await ExecCoreAsync("""
            SELECT c.conname, pg_get_constraintdef(c.oid) AS definition
            FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE c.contype = 'f'
              AND n.nspname = current_schema()
              AND t.relname = @table_name
            """, new (string, object?)[]
        {
            ("table_name", tableName),
        }, ct);

        var matching = rows.Rows.FirstOrDefault(r =>
            (r.GetValueOrDefault("definition") ?? "").Contains(
                $"FOREIGN KEY ({columnName}) REFERENCES {referencedTable}({referencedColumn})",
                StringComparison.OrdinalIgnoreCase));
        if (matching is null) return;

        var definition = matching.GetValueOrDefault("definition") ?? "";
        if (definition.Contains("ON DELETE CASCADE", StringComparison.OrdinalIgnoreCase)) return;

        var existingConstraintName = matching.GetValueOrDefault("conname");
        if (string.IsNullOrWhiteSpace(existingConstraintName)) return;

        await ExecCoreAsync($"ALTER TABLE {tableName} DROP CONSTRAINT {existingConstraintName}", null, ct);
        await ExecCoreAsync($"""
            ALTER TABLE {tableName}
            ADD CONSTRAINT {desiredConstraintName}
            FOREIGN KEY ({columnName}) REFERENCES {referencedTable}({referencedColumn}) ON DELETE CASCADE
            """, null, ct);
    }

    // ─── Verbindung + Authentifizierung ─────────────────────────────────────

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_stream is not null && _tcp is { Connected: true }) return;
        CloseSilently();

        _tcp = new TcpClient();
        await _tcp.ConnectAsync(_s.Host, _s.Port, ct);
        Stream stream = _tcp.GetStream();

        if (_s.SslMode is "prefer" or "require")
        {
            // SSLRequest: len=8, code=80877103
            var req = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(req, 8);
            BinaryPrimitives.WriteInt32BigEndian(req.AsSpan(4), 80877103);
            await stream.WriteAsync(req, ct);
            var answer = new byte[1];
            await stream.ReadExactlyAsync(answer, ct);
            if (answer[0] == (byte)'S')
            {
                var ssl = new SslStream(stream, false, (_, _, _, _) => true); // wie libpq "require": verschlüsselt, ohne CA-Prüfung
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _s.Host }, ct);
                stream = ssl;
            }
            else if (_s.SslMode == "require")
                throw new InvalidOperationException("Server unterstützt kein SSL, aber sslmode=require ist gesetzt.");
        }
        _stream = stream;

        // StartupMessage
        var sb = new MemoryStream();
        WriteInt32(sb, 0);              // Platzhalter Länge
        WriteInt32(sb, 196608);         // Protokoll 3.0
        WriteCString(sb, "user"); WriteCString(sb, _s.User);
        WriteCString(sb, "database"); WriteCString(sb, _s.Database);
        WriteCString(sb, "application_name"); WriteCString(sb, "learncards");
        sb.WriteByte(0);
        var startup = sb.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(startup, startup.Length);
        await _stream.WriteAsync(startup, ct);

        await AuthenticateAsync(ct);
    }

    private async Task AuthenticateAsync(CancellationToken ct)
    {
        ScramState? scram = null;
        while (true)
        {
            var (type, payload) = await ReadMessageAsync(ct);
            switch (type)
            {
                case 'R':
                    var code = BinaryPrimitives.ReadInt32BigEndian(payload);
                    switch (code)
                    {
                        case 0: break;                                   // AuthenticationOk
                        case 3:                                          // Cleartext
                            await SendMessageAsync('p', Encoding.UTF8.GetBytes(_s.Password + "\0"), ct);
                            break;
                        case 5:                                          // MD5
                            var salt = payload.AsSpan(4, 4).ToArray();
                            var md5 = Md5Password(_s.User, _s.Password, salt);
                            await SendMessageAsync('p', Encoding.UTF8.GetBytes(md5 + "\0"), ct);
                            break;
                        case 10:                                         // SASL — Mechanismenliste
                            var mechs = Encoding.UTF8.GetString(payload, 4, payload.Length - 4)
                                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
                            if (!mechs.Contains("SCRAM-SHA-256"))
                                throw new InvalidOperationException($"Server bietet kein SCRAM-SHA-256 an ({string.Join(",", mechs)}).");
                            scram = ScramState.Start();
                            var initial = Encoding.UTF8.GetBytes(scram.ClientFirstMessage);
                            var ms = new MemoryStream();
                            WriteCString(ms, "SCRAM-SHA-256");
                            WriteInt32(ms, initial.Length);
                            ms.Write(initial);
                            await SendMessageAsync('p', ms.ToArray(), ct);
                            break;
                        case 11:                                         // SASLContinue
                            if (scram is null) throw new InvalidOperationException("Unerwartetes SASLContinue.");
                            var serverFirst = Encoding.UTF8.GetString(payload, 4, payload.Length - 4);
                            var final = scram.HandleServerFirst(serverFirst, _s.Password);
                            await SendMessageAsync('p', Encoding.UTF8.GetBytes(final), ct);
                            break;
                        case 12:                                         // SASLFinal
                            if (scram is null) throw new InvalidOperationException("Unerwartetes SASLFinal.");
                            scram.VerifyServerFinal(Encoding.UTF8.GetString(payload, 4, payload.Length - 4));
                            break;
                        default:
                            throw new InvalidOperationException($"Nicht unterstützte Authentifizierungsmethode (Code {code}).");
                    }
                    break;
                case 'S': case 'K': case 'N': break;                     // ParameterStatus / BackendKeyData / Notice
                case 'E': throw new InvalidOperationException("PostgreSQL: " + ParseError(payload));
                case 'Z': return;                                        // ReadyForQuery
                default: break;
            }
        }
    }

    private static string Md5Password(string user, string password, byte[] salt)
    {
        static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
        var inner = Hex(MD5.HashData(Encoding.UTF8.GetBytes(password + user)));
        var outerInput = new byte[inner.Length + 4];
        Encoding.ASCII.GetBytes(inner).CopyTo(outerInput, 0);
        salt.CopyTo(outerInput, inner.Length);
        return "md5" + Hex(MD5.HashData(outerInput));
    }

    private sealed class ScramState
    {
        public required string ClientNonce { get; init; }
        public string ClientFirstBare => $"n=,r={ClientNonce}";
        public string ClientFirstMessage => "n,," + ClientFirstBare;
        private byte[]? _saltedPassword;
        private string? _authMessage;

        public static ScramState Start()
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
            return new ScramState { ClientNonce = nonce };
        }

        public string HandleServerFirst(string serverFirst, string password)
        {
            var parts = serverFirst.Split(',');
            string Get(string key) => parts.First(p => p.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..];
            var serverNonce = Get("r");
            if (!serverNonce.StartsWith(ClientNonce, StringComparison.Ordinal))
                throw new InvalidOperationException("SCRAM: Server-Nonce passt nicht zur Client-Nonce.");
            var salt = Convert.FromBase64String(Get("s"));
            var iterations = int.Parse(Get("i"));

            _saltedPassword = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
            var clientKey = HMACSHA256.HashData(_saltedPassword, Encoding.UTF8.GetBytes("Client Key"));
            var storedKey = SHA256.HashData(clientKey);

            var withoutProof = $"c=biws,r={serverNonce}";
            _authMessage = $"{ClientFirstBare},{serverFirst},{withoutProof}";
            var clientSignature = HMACSHA256.HashData(storedKey, Encoding.UTF8.GetBytes(_authMessage));
            var proof = new byte[clientKey.Length];
            for (var i = 0; i < proof.Length; i++) proof[i] = (byte)(clientKey[i] ^ clientSignature[i]);

            return $"{withoutProof},p={Convert.ToBase64String(proof)}";
        }

        public void VerifyServerFinal(string serverFinal)
        {
            if (_saltedPassword is null || _authMessage is null) throw new InvalidOperationException("SCRAM-Zustand ungültig.");
            var v = serverFinal.Split(',').FirstOrDefault(p => p.StartsWith("v=", StringComparison.Ordinal))?[2..]
                    ?? throw new InvalidOperationException("SCRAM: Server-Signatur fehlt.");
            var serverKey = HMACSHA256.HashData(_saltedPassword, Encoding.UTF8.GetBytes("Server Key"));
            var expected = Convert.ToBase64String(HMACSHA256.HashData(serverKey, Encoding.UTF8.GetBytes(_authMessage)));
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(v), Encoding.ASCII.GetBytes(expected)))
                throw new InvalidOperationException("SCRAM: Server-Signatur ungültig — möglicher MITM.");
        }
    }

    // ─── Extended Query ─────────────────────────────────────────────────────

    private static readonly Regex ParamRegex = new(@"@(\w+)", RegexOptions.Compiled);

    private async Task<(List<Dictionary<string, string?>> Rows, int Affected)> ExecCoreAsync(
        string sql, IReadOnlyList<(string Name, object? Value)>? args, CancellationToken ct)
    {
        // @name → $n übersetzen und Parameterwerte in Auftretens-Reihenfolge sammeln
        var order = new List<string>();
        var translated = ParamRegex.Replace(sql, m =>
        {
            var name = m.Groups[1].Value;
            var idx = order.IndexOf(name);
            if (idx < 0) { order.Add(name); idx = order.Count - 1; }
            return "$" + (idx + 1);
        });

        var values = new string?[order.Count];
        for (var i = 0; i < order.Count; i++)
        {
            var match = args?.FirstOrDefault(a => a.Name == order[i]);
            values[i] = match is { } m2 && m2.Name == order[i] ? DbValue.ToText(m2.Value) : null;
        }

        // Parse
        var parse = new MemoryStream();
        WriteCString(parse, "");                 // unbenanntes Statement
        WriteCString(parse, translated);
        WriteInt16(parse, 0);                    // keine Typangaben → Server inferiert
        await SendMessageAsync('P', parse.ToArray(), ct);

        // Bind
        var bind = new MemoryStream();
        WriteCString(bind, "");                  // Portal
        WriteCString(bind, "");                  // Statement
        WriteInt16(bind, 1); WriteInt16(bind, 0);        // alle Parameter im Textformat
        WriteInt16(bind, (short)values.Length);
        foreach (var v in values)
        {
            if (v is null) WriteInt32(bind, -1);
            else
            {
                var b = Encoding.UTF8.GetBytes(v);
                WriteInt32(bind, b.Length);
                bind.Write(b);
            }
        }
        WriteInt16(bind, 1); WriteInt16(bind, 0);        // Ergebnisse im Textformat
        await SendMessageAsync('B', bind.ToArray(), ct);

        // Describe Portal + Execute + Sync
        var describe = new MemoryStream(); describe.WriteByte((byte)'P'); WriteCString(describe, "");
        await SendMessageAsync('D', describe.ToArray(), ct);
        var execute = new MemoryStream(); WriteCString(execute, ""); WriteInt32(execute, 0);
        await SendMessageAsync('E', execute.ToArray(), ct);
        await SendMessageAsync('S', Array.Empty<byte>(), ct);

        // Antworten lesen
        var rows = new List<Dictionary<string, string?>>();
        string[]? columns = null;
        var affected = 0;
        string? error = null;

        while (true)
        {
            var (type, payload) = await ReadMessageAsync(ct);
            switch (type)
            {
                case '1': case '2': case 'n': case 't': break;   // ParseComplete/BindComplete/NoData/ParameterDescription
                case 'T':
                    var count = BinaryPrimitives.ReadInt16BigEndian(payload);
                    columns = new string[count];
                    var off = 2;
                    for (var i = 0; i < count; i++)
                    {
                        var end = Array.IndexOf(payload, (byte)0, off);
                        columns[i] = Encoding.UTF8.GetString(payload, off, end - off);
                        off = end + 1 + 18;                       // tableOid(4)+attnum(2)+typOid(4)+typlen(2)+typmod(4)+format(2)
                    }
                    break;
                case 'D':
                    var ncols = BinaryPrimitives.ReadInt16BigEndian(payload);
                    var row = new Dictionary<string, string?>(ncols, StringComparer.OrdinalIgnoreCase);
                    var pos = 2;
                    for (var i = 0; i < ncols; i++)
                    {
                        var len = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(pos)); pos += 4;
                        string? val = null;
                        if (len >= 0) { val = Encoding.UTF8.GetString(payload, pos, len); pos += len; }
                        row[columns is not null && i < columns.Length ? columns[i] : $"col{i}"] = val;
                    }
                    rows.Add(row);
                    break;
                case 'C':
                    var tag = Encoding.UTF8.GetString(payload).TrimEnd('\0');
                    var lastSpace = tag.LastIndexOf(' ');
                    if (lastSpace > 0 && int.TryParse(tag[(lastSpace + 1)..], out var n)) affected = n;
                    break;
                case 'E': error = ParseError(payload); break;     // bis ReadyForQuery weiterlesen
                case 'N': case 'S': case 'K': break;
                case 'Z':
                    if (error is not null) throw new InvalidOperationException("PostgreSQL: " + error);
                    return (rows, affected);
            }
        }
    }

    // ─── Wire-Helfer ────────────────────────────────────────────────────────

    private async Task SendMessageAsync(char type, byte[] payload, CancellationToken ct)
    {
        var buf = new byte[1 + 4 + payload.Length];
        buf[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(1), payload.Length + 4);
        payload.CopyTo(buf, 5);
        await _stream!.WriteAsync(buf, ct);
    }

    private async Task<(char Type, byte[] Payload)> ReadMessageAsync(CancellationToken ct)
    {
        var header = new byte[5];
        await _stream!.ReadExactlyAsync(header, ct);
        var len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)) - 4;
        var payload = len > 0 ? new byte[len] : Array.Empty<byte>();
        if (len > 0) await _stream.ReadExactlyAsync(payload, ct);
        return ((char)header[0], payload);
    }

    private static string ParseError(byte[] payload)
    {
        var fields = new Dictionary<char, string>();
        var pos = 0;
        while (pos < payload.Length && payload[pos] != 0)
        {
            var code = (char)payload[pos++];
            var end = Array.IndexOf(payload, (byte)0, pos);
            if (end < 0) break;
            fields[code] = Encoding.UTF8.GetString(payload, pos, end - pos);
            pos = end + 1;
        }
        fields.TryGetValue('S', out var severity);
        fields.TryGetValue('M', out var message);
        fields.TryGetValue('D', out var detail);
        return $"{severity}: {message}{(detail is null ? "" : " — " + detail)}";
    }

    private static void WriteInt32(Stream s, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        s.Write(b);
    }

    private static void WriteInt16(Stream s, short v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(b, v);
        s.Write(b);
    }

    private static void WriteCString(Stream s, string v)
    {
        var b = Encoding.UTF8.GetBytes(v);
        s.Write(b);
        s.WriteByte(0);
    }

    private void CloseSilently()
    {
        try { _stream?.Dispose(); } catch { /* ignorieren */ }
        try { _tcp?.Dispose(); } catch { /* ignorieren */ }
        _stream = null; _tcp = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_stream is not null)
            {
                try { await SendMessageAsync('X', Array.Empty<byte>(), CancellationToken.None); } catch { /* ignorieren */ }
            }
            CloseSilently();
        }
        finally { _gate.Release(); }
    }
}
