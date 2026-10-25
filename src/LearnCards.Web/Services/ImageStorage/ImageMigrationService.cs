using System.Security.Cryptography;
using LearnCards.Web.Data;
using LearnCards.Web.Infrastructure;

namespace LearnCards.Web.Services.ImageStorage;

public sealed record MigrationError(string StorageKey, string Reason);

public sealed record MigrationResult(string Source, string Target, bool DryRun, int Processed, int Migrated, int Failed, long Bytes, int Remaining, string? NextCursor, List<MigrationError> Errors);

/// <summary>
/// Ausdrücklich gestartete, wiederaufnehmbare Migration von Bildern zwischen Providern (ADR 0017). Läuft nie automatisch — auch nicht bei einem Providerwechsel.
/// Je Speicherobjekt: lesen → Prüfsumme gegen die gespeicherte vergleichen → in das Ziel schreiben → zurücklesen und Prüfsumme erneut vergleichen → erst dann die
/// Metadaten (<c>card_images.provider</c>) umstellen. Die <b>Quelle wird nie gelöscht</b> (Aufräumen ist eine bewusste manuelle Entscheidung nach der Prüfung).
/// Aufrufe arbeiten höchstens <c>limit</c> Objekte ab und liefern einen Cursor; ein erneuter Aufruf setzt fort. Bereits migrierte Objekte werden nicht erneut angefasst.
/// </summary>
public sealed class ImageMigrationService
{
    private readonly IDatabase _db;
    private readonly ImageStorageRegistry _registry;
    private readonly SemaphoreSlim _running = new(1, 1);

    public ImageMigrationService(IDatabase db, ImageStorageRegistry registry)
    {
        _db = db;
        _registry = registry;
    }

    public async Task<Dictionary<string, int>> CountByProviderAsync()
    {
        var rows = await _db.QueryAsync("SELECT provider, COUNT(*) AS n FROM card_images GROUP BY provider");
        return rows.ToDictionary(r => r["provider"] ?? "", r => int.Parse(r["n"] ?? "0"));
    }

    public async Task<MigrationResult> MigrateAsync(string source, string target, int limit, string? cursor, bool dryRun, CancellationToken ct = default)
    {
        if (!ImageProviderNames.All.Contains(source) || !ImageProviderNames.All.Contains(target))
            throw new ArgumentException("Unbekannter Provider. Erlaubt: " + string.Join(", ", ImageProviderNames.All));
        if (source == target) throw new ArgumentException("Quelle und Ziel müssen verschieden sein.");
        if (!_registry.IsConfigured(source) || !_registry.IsConfigured(target))
            throw new ArgumentException("Quelle und Ziel müssen konfiguriert sein (IMAGE_* Umgebungsvariablen).");
        if (cursor is not null && !ImageStorageKey.IsValid(cursor)) throw new ArgumentException("Ungültiger Cursor.");
        limit = Math.Clamp(limit, 1, 2000);

        if (!await _running.WaitAsync(0, ct)) throw new InvalidOperationException("Es läuft bereits eine Migration.");
        try
        {
            var src = _registry.Get(source);
            var dst = _registry.Get(target);
            var objects = await _db.QueryAsync(
                "SELECT storage_key, MAX(checksum) AS checksum FROM card_images WHERE provider = @p AND storage_key > @after GROUP BY storage_key ORDER BY storage_key LIMIT " + limit,
                new[] { ("p", (object?)source), ("after", cursor ?? "") }, ct);

            int migrated = 0, failed = 0; long bytes = 0;
            var errors = new List<MigrationError>();
            string? next = null;
            foreach (var o in objects)
            {
                ct.ThrowIfCancellationRequested();
                var key = o["storage_key"] ?? "";
                next = key;
                try
                {
                    var checksum = o["checksum"] ?? "";
                    var stored = await src.GetAsync(key, null, ct) ?? throw new InvalidOperationException("im Quellspeicher nicht gefunden");
                    var actual = Hex(stored.Data);
                    if (checksum.Length > 0 && !string.Equals(actual, checksum, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Prüfsumme der Quelle stimmt nicht");
                    if (!dryRun)
                    {
                        await dst.PutAsync(key, stored.Data, "application/octet-stream", ct);
                        var back = await dst.GetAsync(key, null, ct) ?? throw new InvalidOperationException("nach dem Schreiben im Ziel nicht lesbar");
                        if (!string.Equals(Hex(back.Data), actual, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Prüfsumme im Ziel stimmt nicht");
                        await _db.ExecuteAsync("UPDATE card_images SET provider = @t WHERE provider = @s AND storage_key = @k",
                            new[] { ("t", (object?)target), ("s", (object?)source), ("k", (object?)key) }, ct);
                    }
                    migrated++;
                    bytes += stored.Data.Length;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    failed++;
                    errors.Add(new MigrationError(key, SecretRedactor.Redact(e.Message)));   // Quelle bleibt unangetastet; erneuter Lauf möglich
                }
            }

            var remainingRows = await _db.QueryAsync("SELECT COUNT(DISTINCT storage_key) AS n FROM card_images WHERE provider = @p AND storage_key > @after",
                new[] { ("p", (object?)source), ("after", next ?? cursor ?? "") }, ct);
            var remaining = int.Parse(remainingRows[0]["n"] ?? "0");
            return new MigrationResult(source, target, dryRun, objects.Count, migrated, failed, bytes, remaining, remaining > 0 ? next : null, errors);
        }
        finally { _running.Release(); }
    }

    private static string Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
