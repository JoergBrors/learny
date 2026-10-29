namespace LearnCards.Web.Services.Certificates;

/// <summary>
/// Formatversion eines ausgestellten Credentials (für Oberfläche und Register): <b>Format 2</b> = VC-JWT-Profil nach ADR 0038 (typ JWT, Credential im Claim vc, Compact-JWS im CLR);
/// <b>Format 1</b> = Altbestand (typ vc+jwt, Hülle JwtProof2020). Format 1 wird nie umsigniert; die Kennzeichnung zeigt, welche Nachweise noch Altlasten sind.
/// </summary>
public static class CertificateFormat
{
    public const int Current = 2;

    public static int Of(string? badgeJws) => string.IsNullOrEmpty(badgeJws) || BadgeSigner.IsLegacyProfile(badgeJws) ? 1 : 2;

    public static string Label(int format) => format >= Current ? "Format 2 (VC-JWT, ADR 0038)" : "Format 1 (Altbestand, vc+jwt)";
}
