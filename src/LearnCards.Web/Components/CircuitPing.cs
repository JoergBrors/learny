using Microsoft.JSInterop;

namespace LearnCards.Web.Components;

/// <summary>
/// Lebenszeichen der Blazor-Sitzung für <c>ReconnectModal.razor.js</c>: Antwortet der Aufruf, ist die Verbindung der Sitzung intakt
/// (mobile Browser halten WebSockets im Hintergrund an, sie wirken dann offen, sind aber tot). Kein Zustand, keine Daten, kein Benutzerbezug.
/// </summary>
public static class CircuitPing
{
    [JSInvokable("Ping")]
    public static bool Ping() => true;
}
