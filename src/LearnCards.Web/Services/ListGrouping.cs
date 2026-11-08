using System.Globalization;

namespace LearnCards.Web.Services;

/// <summary>Eine Gruppe einer gruppierten Liste. Leerer Titel = keine Gruppierung.</summary>
public sealed record ListGroup<T>(string Title, IReadOnlyList<T> Items);

/// <summary>Gruppiert bereits sortierte Listen (die Reihenfolge innerhalb einer Gruppe bleibt erhalten). Reine Darstellung, keine Berechtigungslogik.</summary>
public static class ListGrouping
{
    private static readonly CultureInfo De = new("de-DE");

    /// <param name="key">Reihenfolge und Titel der Gruppe je Eintrag; <c>null</c> = eine Gruppe ohne Titel.</param>
    public static List<ListGroup<T>> By<T>(IReadOnlyList<T> items, Func<T, (int Order, string Title)>? key)
    {
        if (key is null) return new() { new ListGroup<T>("", items) };
        return items.Select(i => (Key: key(i), Item: i)).GroupBy(x => x.Key.Title)
            .OrderBy(g => g.First().Key.Order).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new ListGroup<T>(g.Key, g.Select(x => x.Item).ToList())).ToList();
    }

    /// <summary>Monatsgruppe (neueste zuerst), z. B. „November 2026“.</summary>
    public static (int Order, string Title) Month(DateTime utc)
    {
        var d = utc.ToLocalTime();
        return (int.MaxValue - (d.Year * 12 + d.Month), d.ToString("MMMM yyyy", De));
    }
}
