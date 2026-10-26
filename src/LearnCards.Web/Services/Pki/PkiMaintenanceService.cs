namespace LearnCards.Web.Services.Pki;

/// <summary>Hintergrundwartung der Learny-PKI (ADR 0035): CRL der Issuing CA vor <c>nextUpdate</c> erneuern, abgelaufene Zertifikate markieren, Signaturzertifikat kurz vor Ablauf erneuern.</summary>
public sealed class PkiMaintenanceService(PkiService pki, ILogger<PkiMaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var done = await pki.RunMaintenanceAsync();
                if (done.Count > 0) log.LogInformation("PKI-Wartung: {Actions}", string.Join(", ", done));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError("PKI-Wartung fehlgeschlagen ({Type}).", ex.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
