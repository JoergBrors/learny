using LearnCards.Web.Domain;
using LearnCards.Web.Services;

/// <summary>Avatar-Baukasten und Generator (ADR 0051): nur geprüfte Parameter, Offline-Fallback, Rückweisung ungültiger Werte.</summary>
public static class AvatarUnitTests
{
    public static void Run(Action<bool, string> check)
    {
        check(Avatars.Find("luna") is not null && Avatars.Find("gibtsnicht") is null, "Avatar: Voreinstellungen werden aufgelöst, unbekannte nicht");
        check(Avatars.Custom("#F5D0B5", "#C2410C", "curly", "#7C3AED", "glasses") is { } c && Avatars.Find(c.Id)?.HairStyle == "curly", "Avatar: eigener Avatar ist über seine Kennung wieder auflösbar");
        foreach (var bad in new[] { "custom:red|#000000|short|#7C3AED|none", "custom:#F5D0B5|#C2410C|afro|#7C3AED|none", "custom:#F5D0B5|#C2410C|short|#7C3AED|<script>",
                                    "custom:#F5D0B5\" onload=\"x|#C2410C|short|#7C3AED|none", "custom:#F5D0B5|#C2410C|short|#7C3AED", "custom:" })
            check(Avatars.Find(bad) is null, "Avatar: ungültige Parameter werden abgewiesen (" + bad[..Math.Min(24, bad.Length)] + ")");

        var r = AvatarGeneratorService.FromKeywords("freundliche Person mit roten Locken, Brille und grünem Pullover");
        var p = Avatars.Find(r.Id);
        check(p is not null && !r.UsedAi && p.HairStyle == "curly" && p.Accessory == "glasses" && p.Hair == "#C2410C" && p.Outfit == "#10B981",
            "Avatar-Generator (Offline): Locken, Brille, rote Haare, grüner Pullover werden erkannt (Haar- und Kleidungsfarbe getrennt)");
        var d = Avatars.Find(AvatarGeneratorService.FromKeywords("irgendwas").Id);
        check(d is not null && d.HairStyle == "short" && d.Accessory == "none", "Avatar-Generator (Offline): unbekannte Beschreibung ergibt einen gültigen Standardavatar");
        foreach (var s in Avatars.States) check(Avatars.States.Contains(s), "Avatar: Stimmung " + s + " zulässig");
    }
}
