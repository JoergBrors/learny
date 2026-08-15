namespace LearnCards.Web.Infrastructure;

/// <summary>
/// Lädt eine .env-Datei (Repo-Wurzel oder Arbeitsverzeichnis) in die Prozess-Umgebung.
/// Bereits gesetzte Umgebungsvariablen haben Vorrang (Docker/Azure App Settings gewinnen).
/// </summary>
public static class EnvLoader
{
    public static string? LoadedFrom { get; private set; }

    public static void Load(string? explicitPath = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath);
        var envFile = Environment.GetEnvironmentVariable("ENV_FILE");
        if (!string.IsNullOrWhiteSpace(envFile)) candidates.Add(envFile);

        // Vom aktuellen Verzeichnis aufwärts suchen (deckt "dotnet run" in src/LearnCards.Web ab)
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 4 && dir is not null; i++, dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, ".env"));

        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase)) line = line[7..].Trim();

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            // Umschließende Anführungszeichen entfernen
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                value = value[1..^1];
            else
            {
                // Inline-Kommentar nur bei unquoted Werten (mit Leerzeichen davor)
                var hash = value.IndexOf(" #", StringComparison.Ordinal);
                if (hash >= 0) value = value[..hash].TrimEnd();
            }

            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
        LoadedFrom = path;
    }
}
