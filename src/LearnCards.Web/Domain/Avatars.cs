namespace LearnCards.Web.Domain;

/// <summary>Eine vordefinierte Avatar-Variante (ADR 0051). Rein dekorativ: leitet nichts aus der Identität ab und verleiht keine Rechte.</summary>
public sealed record AvatarPreset(string Id, string Name, string Skin, string Hair, string HairStyle, string Outfit, string Accessory);

/// <summary>Vordefinierte, selbst gezeichnete Lernbegleiter (SVG, ohne externe Dienste). Unterschiedliche Hauttöne, Frisuren, Kleidung und Accessoires; keine Ableitung persönlicher Merkmale.</summary>
public static class Avatars
{
    public static readonly IReadOnlyList<AvatarPreset> All = new AvatarPreset[]
    {
        new("luna",  "Luna",  "#F5D0B5", "#C2410C", "long",  "#7C3AED", "beanie"),
        new("noah",  "Noah",  "#E8B58D", "#3B2314", "short", "#0EA5A4", "glasses"),
        new("amira", "Amira", "#B9805B", "#1F1A17", "curly", "#F59E0B", "none"),
        new("jonas", "Jonas", "#F8DDC8", "#D6A441", "short", "#EC4899", "headphones"),
        new("kai",   "Kai",   "#8D5A3C", "#111827", "bun",   "#6366F1", "glasses"),
        new("sofia", "Sofia", "#EEC3A0", "#6B2D1F", "long",  "#10B981", "none"),
        new("ben",   "Ben",   "#D9A27C", "#4B5563", "short", "#F43F5E", "cap"),
        new("yuki",  "Yuki",  "#F3D2B8", "#0F172A", "bob",   "#A78BFA", "headphones"),
    };

    /// <summary>Zulässige Zustände (Stimmung) der Darstellung.</summary>
    public static readonly IReadOnlySet<string> States = new HashSet<string> { "neutral", "happy", "motivating", "success", "encouraging" };

    public static readonly IReadOnlySet<string> HairStyles = new HashSet<string> { "short", "long", "bob", "curly", "bun" };
    public static readonly IReadOnlySet<string> Accessories = new HashSet<string> { "none", "beanie", "cap", "headphones", "glasses" };
    private const string CustomPrefix = "custom:";
    private static readonly System.Text.RegularExpressions.Regex HexColor = new("^#[0-9A-Fa-f]{6}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Löst eine Voreinstellung oder einen selbst erzeugten Avatar („custom:Haut|Haar|Frisur|Kleidung|Accessoire“) auf; ungültige Werte ergeben null.</summary>
    public static AvatarPreset? Find(string? id)
    {
        if (id is not null && id.StartsWith(CustomPrefix, StringComparison.Ordinal))
        {
            var parts = id[CustomPrefix.Length..].Split('|');
            return parts.Length == 5 ? Custom(parts[0], parts[1], parts[2], parts[3], parts[4]) : null;
        }
        return All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
    }

    /// <summary>Baut einen eigenen Avatar nur aus geprüften Werten (Hexfarben, feste Listen) — nie aus freiem Text/SVG.</summary>
    public static AvatarPreset? Custom(string skin, string hair, string style, string outfit, string accessory)
    {
        if (!HexColor.IsMatch(skin) || !HexColor.IsMatch(hair) || !HexColor.IsMatch(outfit) || !HairStyles.Contains(style) || !Accessories.Contains(accessory)) return null;
        return new AvatarPreset(Encode(skin, hair, style, outfit, accessory), "Eigener Avatar", skin.ToUpperInvariant(), hair.ToUpperInvariant(), style, outfit.ToUpperInvariant(), accessory);
    }

    public static string Encode(string skin, string hair, string style, string outfit, string accessory) =>
        $"{CustomPrefix}{skin.ToUpperInvariant()}|{hair.ToUpperInvariant()}|{style}|{outfit.ToUpperInvariant()}|{accessory}";
    public static bool IsValid(string? id) => Find(id) is not null;
}
