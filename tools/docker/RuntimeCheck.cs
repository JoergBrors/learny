// Mindestversions-Check für .NET-Laufzeiten in Container-Images (CVE-2026-111497, ADR 0024/0036).
// Liest die Ausgabe von `dotnet --list-runtimes` von der Standardeingabe und prüft numerisch (nie per Stringvergleich),
// dass jedes geforderte Framework in derselben Hauptversion wie --min mindestens in der Mindestversion vorhanden ist.
// Höhere Patchversionen bestehen; Vorabversionen (z. B. 10.0.14-rc.1) zählen nicht. Keine NuGet-Pakete (ADR 0002).
//
// Aufruf:  docker run --rm --entrypoint dotnet <image> --list-runtimes | dotnet run tools/docker/RuntimeCheck.cs -- --min 10.0.14 --require Microsoft.NETCore.App,Microsoft.AspNetCore.App --label web
// Exit-Code: 0 = bestanden, 1 = Laufzeit zu alt oder fehlt, 2 = falsche Verwendung.

#:property PublishAot=false
#:property PublishTrimmed=false

using System.Text.RegularExpressions;

string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

var minText = Arg("--min");
var required = (Arg("--require") ?? "Microsoft.NETCore.App").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var label = Arg("--label") ?? "image";
if (minText is null || !Version.TryParse(minText, out var min) || min.Build < 0)
{
    Console.Error.WriteLine("Verwendung: --min <Major.Minor.Patch> [--require A,B] [--label Name]   (Eingabe: dotnet --list-runtimes)");
    return 2;
}

var found = new Dictionary<string, List<Version>>();
foreach (var line in Console.In.ReadToEnd().Split('\n'))
{
    var m = Regex.Match(line.Trim(), @"^(?<name>[A-Za-z0-9.]+) (?<ver>\S+) \[");
    if (!m.Success) continue;
    if (!Version.TryParse(m.Groups["ver"].Value, out var v)) continue;   // Vorabversionen ("10.0.14-rc.1") lassen sich nicht parsen und zählen nicht
    if (!found.TryGetValue(m.Groups["name"].Value, out var list)) found[m.Groups["name"].Value] = list = new();
    list.Add(v);
}

var failed = false;
foreach (var fw in required)
{
    var best = found.GetValueOrDefault(fw)?.Where(v => v.Major == min.Major).OrderDescending().FirstOrDefault();
    if (best is null) { Console.Error.WriteLine($"[{label}] FEHLER: {fw} {min.Major}.x nicht gefunden."); failed = true; }
    else if (best < min) { Console.Error.WriteLine($"[{label}] FEHLER: {fw} {best} ist älter als die bereinigte Mindestversion {min} (CVE-2026-111497)."); failed = true; }
    else Console.WriteLine($"[{label}] OK: {fw} {best} >= {min}");
}
return failed ? 1 : 0;
