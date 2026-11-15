namespace LearnCards.Web.Services;

/// <summary>
/// Darstellungszustand des angemeldeten Benutzers innerhalb einer Blazor-Sitzung (Avatar-Voreinstellung). Wird vom Layout geladen und von der Profilseite geändert;
/// alle Avatar-Anzeigen reagieren über <see cref="Changed"/>. Reine Darstellung, keine Berechtigungsentscheidung.
/// </summary>
public sealed class UserUiState
{
    public string AvatarPreset { get; private set; } = "";
    public bool AvatarHidden { get; private set; }
    /// <summary>Stimmungen, für die die aktive KI-Figur ein eigenes Bild besitzt (leer bei Baukasten-Avataren); steuert den Hinweis bei fehlenden Varianten.</summary>
    public IReadOnlySet<string> AvatarMoods { get; private set; } = new HashSet<string>();
    public event Action? Changed;

    public void SetMoods(IReadOnlySet<string> moods) { AvatarMoods = moods; Changed?.Invoke(); }

    public void SetAvatar(string preset, bool hidden)
    {
        if (preset == AvatarPreset && hidden == AvatarHidden) return;
        AvatarPreset = preset; AvatarHidden = hidden;
        Changed?.Invoke();
    }
}
