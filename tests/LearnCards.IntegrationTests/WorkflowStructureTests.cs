/// <summary>
/// IIS-Workflow (ADR 0029): Reihenfolge und Absicherung des MCP-Neustarts. Statische Prüfung des Workflow-Textes — die Ausführung selbst braucht einen Windows-Runner
/// mit IIS und ist hier nicht ausführbar.
/// </summary>
public static class WorkflowStructureTests
{
    public static void Run(Action<bool, string> check, string repoRoot)
    {
        Console.WriteLine("\nIIS-Workflow: MCP-Neustart (ADR 0029)");
        var text = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "deploy-iis-on-tag.yml")).Replace("\r\n", "\n");
        var install = Step(text, "Install MCP Deployment Package");
        var configure = Step(text, "Configure MCP Background Task");
        var verify = Step(text, "Verify MCP HTTP Service");

        check(install.Contains("Get-Service -Name $taskName") && install.IndexOf("Get-Service -Name $taskName", StringComparison.Ordinal) < install.IndexOf("Stop-Service", StringComparison.Ordinal)
              && install.IndexOf("Stop-Service", StringComparison.Ordinal) < install.IndexOf("robocopy $extractPath", StringComparison.Ordinal), "W1 Zustand wird ermittelt und ein vorhandener Windows-Dienst vor dem Ersetzen der Dateien gestoppt");
        check(install.Contains("WaitForStatus(\"Stopped\"") && install.Contains("Get-McpProcesses).Count -gt 0") && install.IndexOf("konnte nicht beendet werden", StringComparison.Ordinal) < install.IndexOf("robocopy $extractPath", StringComparison.Ordinal)
              && install.Contains("NICHT ersetzt"), "W2 es wird gewartet, bis der MCP-Prozess beendet ist; sonst bricht das Deployment ab, bevor Dateien ersetzt werden");
        check(configure.Contains("$serviceUsable") && configure.Contains("Win32_Service") && configure.Contains("Start-Service -Name $taskName") && configure.Contains("WaitForStatus(\"Running\"") && configure.Contains("keinen MCP-Prozess"),
            "W3 ein vorhandener Windows-Dienst wird nach dem Update gestartet, auf „Running“ geprüft und muss einen MCP-Prozess besitzen");
        var guardIdx = configure.IndexOf("if ($serviceUsable)", StringComparison.Ordinal);
        check(configure.IndexOf("Start-Service", StringComparison.Ordinal) > guardIdx && !configure.Contains("New-Service") && !configure.Contains("sc.exe create"), "W4 ein nicht vorhandener Dienst wird nie gestartet oder angelegt (nur bei vorhandenem Dienst)");
        check(configure.Contains("if (-not $httpEnabled)") && configure.IndexOf("if (-not $httpEnabled)", StringComparison.Ordinal) < guardIdx, "W5 im reinen stdio-Modus (HTTP deaktiviert) wird nichts gestartet; die Aufgabe wird entfernt");
        check(configure.Contains("schtasks.exe /Run") && configure.Contains("innerhalb von 45 s nicht gestartet") && configure.Contains("Backup des Runners"), "W6 geplante Aufgabe: wird gestartet, auf den Prozess gewartet; Fehlstart bricht das Deployment mit nachvollziehbarer Meldung ab");
        check(verify.Contains("-le 12") && verify.Contains("Start-Sleep -Seconds 5") && verify.Contains("throw \"MCP-Health-Check fehlgeschlagen (12 Versuche)"), "W7 Healthcheck mit Wiederholungen; dauerhafter Fehler lässt das Deployment fehlschlagen (kein stiller Erfolg)");
        check(!install.Contains("MCP_API_KEY") && !configure.Contains("TOKEN_KEY") && !verify.Contains("Write-Host $env:DEPLOY_"), "W8 keine Geheimnisse in den MCP-Schritten des Workflows");
        check(install.IndexOf("Expand-Archive", StringComparison.Ordinal) < install.IndexOf("Stop-Service", StringComparison.Ordinal) && install.Contains("mcpsettings.json"), "W9 Konfiguration (mcpsettings.json) wird weiterhin gesichert und wiederhergestellt");
        // WebSocket-Protokoll (Betrieb): Feature idempotent installieren, nie Neustart, Verifikation mit echtem Handshake
        var ensure = Step(text, "Ensure IIS And Deployment Prerequisites");
        var ws = Step(text, "Verify WebSocket Transport");
        check(ensure.Contains("Get-WindowsFeature -Name Web-WebSockets") && ensure.Contains("InstallState -eq \"Installed\"") && ensure.Contains("Install-WindowsFeature -Name Web-WebSockets") && !ensure.Contains("-Restart"),
            "W10 Web-WebSockets wird nur bei fehlender Installation installiert (idempotent), nie mit automatischem Neustart; erforderlicher Neustart wird gemeldet");
        check(ws.Contains("/_blazor/negotiate") && ws.Contains("ClientWebSocket") && ws.Contains("ConnectAsync") && ws.Contains("connectionToken") && ws.Contains("IIS_REQUIRE_WEBSOCKETS"),
            "W11 Verifikation prüft Negotiate und einen echten WebSocket-Handshake (nicht nur die Transportliste); Schärfe über IIS_REQUIRE_WEBSOCKETS");
        check(ws.IndexOf("Verify WebSocket Transport", StringComparison.Ordinal) > 0 && text.IndexOf("- name: Verify Deployment", StringComparison.Ordinal) < text.IndexOf("- name: Verify WebSocket Transport", StringComparison.Ordinal) && !ws.Contains("connectionToken)\"") && !ws.Contains("Write-Host $n.connectionToken"),
            "W12 WebSocket-Prüfung läuft nach dem Health-Check und gibt das Negotiate-Token nie aus");
    }

    private static string Step(string text, string name)
    {
        var start = text.IndexOf("- name: " + name, StringComparison.Ordinal);
        if (start < 0) return "";
        var next = text.IndexOf("\n      - name: ", start + 10, StringComparison.Ordinal);
        return next < 0 ? text[start..] : text[start..next];
    }
}
