using System.Diagnostics;

namespace LearnCards.Web.Data;

/// <summary>
/// Nur Development: zählt Datenbankabfragen und misst deren Dauer je HTTP-Anfrage (Server-Timing-Header, Middleware in Program.cs).
/// Protokolliert/erfasst weder SQL-Text noch Parameterwerte, nur Anzahl und Dauer. Im Produktivbetrieb nicht registriert (kein Mehraufwand).
/// </summary>
public sealed class TimingDatabase : IDatabase
{
    private readonly IDatabase _inner;
    public TimingDatabase(IDatabase inner) => _inner = inner;

    public string ProviderName => _inner.ProviderName;
    public Task InitAsync(CancellationToken ct = default) => _inner.InitAsync(ct);
    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    public async Task<List<Dictionary<string, string?>>> QueryAsync(string sql, IReadOnlyList<(string Name, object? Value)>? args = null, CancellationToken ct = default)
    {
        var t = Stopwatch.GetTimestamp();
        try { return await _inner.QueryAsync(sql, args, ct); } finally { RequestTiming.Current?.Add(Stopwatch.GetElapsedTime(t)); }
    }

    public async Task<int> ExecuteAsync(string sql, IReadOnlyList<(string Name, object? Value)>? args = null, CancellationToken ct = default)
    {
        var t = Stopwatch.GetTimestamp();
        try { return await _inner.ExecuteAsync(sql, args, ct); } finally { RequestTiming.Current?.Add(Stopwatch.GetElapsedTime(t)); }
    }

    public async Task<int[]> ExecuteBatchAsync(IReadOnlyList<DbStatement> statements, CancellationToken ct = default)
    {
        var t = Stopwatch.GetTimestamp();
        try { return await _inner.ExecuteBatchAsync(statements, ct); } finally { RequestTiming.Current?.Add(Stopwatch.GetElapsedTime(t)); }
    }
}

/// <summary>Zähler je Anfrage (AsyncLocal): Anzahl und Gesamtdauer der Datenbankaufrufe.</summary>
public sealed class RequestTiming
{
    private static readonly AsyncLocal<RequestTiming?> Holder = new();
    public static RequestTiming? Current => Holder.Value;
    public static RequestTiming Begin() { var r = new RequestTiming(); Holder.Value = r; return r; }
    private long _ticks; private int _count;
    public int Count => _count;
    public double DbMs => TimeSpan.FromTicks(Interlocked.Read(ref _ticks)).TotalMilliseconds;
    public void Add(TimeSpan d) { Interlocked.Add(ref _ticks, d.Ticks); Interlocked.Increment(ref _count); }
}
