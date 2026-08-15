namespace LearnCards.McpServer;

/// <summary>Minimaler .env-Loader (identische Logik wie im Web-Projekt, hier dupliziert um die
/// MCP-Server-Konsole ohne Projektreferenz eigenständig lauffähig zu halten).</summary>
public static class EnvLoader
{
    public static void Load()
    {
        var candidates = new List<string>();
        var envFile = Environment.GetEnvironmentVariable("ENV_FILE");
        if (!string.IsNullOrWhiteSpace(envFile)) candidates.Add(envFile);

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 5 && dir is not null; i++, dir = dir.Parent)
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
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                value = value[1..^1];

            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
