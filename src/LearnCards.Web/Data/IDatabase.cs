namespace LearnCards.Web.Data;

/// <summary>
/// Minimale Datenbank-Abstraktion (bewusst ohne externe NuGet-Abhängigkeiten).
/// Werte werden im Textformat übertragen; die Repository-Schicht übernimmt die Typkonvertierung.
/// Parameter werden als @name notiert und immer gebunden (kein String-Concat → kein SQL-Injection-Risiko).
/// </summary>
public interface IDatabase : IAsyncDisposable
{
    string ProviderName { get; }
    Task InitAsync(CancellationToken ct = default);
    Task<List<Dictionary<string, string?>>> QueryAsync(string sql, IReadOnlyList<(string Name, object? Value)>? args = null, CancellationToken ct = default);
    Task<int> ExecuteAsync(string sql, IReadOnlyList<(string Name, object? Value)>? args = null, CancellationToken ct = default);

    /// <summary>
    /// Führt mehrere Anweisungen <b>atomar</b> in einer Transaktion aus (alles oder nichts, ADR 0049). Weicht die Zahl der betroffenen Zeilen von
    /// <see cref="DbStatement.ExpectedRows"/> ab oder schlägt eine Anweisung fehl, wird zurückgerollt; bei Abweichung folgt <see cref="DbBatchConflictException"/>.
    /// Bricht die Verbindung während des Commit ab, ist der Ausgang unbekannt — Aufrufer müssen den Zustand idempotent nachprüfen.
    /// Standardmäßig nicht unterstützt (kein stilles, nichtatomares Ausführen).
    /// </summary>
    Task<int[]> ExecuteBatchAsync(IReadOnlyList<DbStatement> statements, CancellationToken ct = default) =>
        throw new NotSupportedException("Dieser Datenbankprovider unterstützt keine atomaren Anweisungsgruppen.");
}

/// <summary>Eine gebundene Anweisung für <see cref="IDatabase.ExecuteBatchAsync"/>. <paramref name="ExpectedRows"/> = erwartete Zahl betroffener Zeilen (z. B. 1 für den atomaren Anspruch).</summary>
public sealed record DbStatement(string Sql, IReadOnlyList<(string Name, object? Value)>? Args = null, int? ExpectedRows = null);

/// <summary>Eine Anweisung der Gruppe betraf nicht die erwartete Zahl von Zeilen (z. B. der Anspruch auf eine Einladung wurde von einem anderen Aufruf gewonnen). Die Gruppe wurde zurückgerollt.</summary>
public sealed class DbBatchConflictException : InvalidOperationException
{
    public int Index { get; }
    public DbBatchConflictException(int index, int expected, int actual) : base($"Anweisung {index}: {actual} statt {expected} Zeilen betroffen.") => Index = index;
}

public static class DbValue
{
    /// <summary>Konvertiert .NET-Werte in das Text-Wireformat beider Provider.</summary>
    public static string? ToText(object? value) => value switch
    {
        null => null,
        bool b => b ? "1" : "0",
        DateTime dt => dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        float f => f.ToString(System.Globalization.CultureInfo.InvariantCulture),
        decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture),
        IFormattable fmt => fmt.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    public static bool ToBool(string? s) => s is "1" or "t" or "true" or "True";
    public static int ToInt(string? s) => int.TryParse(s, out var i) ? i : 0;
    public static double ToDouble(string? s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
    public static DateTime ToDateTime(string? s) =>
        DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
            ? dt : DateTime.MinValue;
}
