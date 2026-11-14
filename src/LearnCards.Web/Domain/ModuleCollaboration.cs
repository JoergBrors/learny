namespace LearnCards.Web.Domain;

// ─── Modulbezogene Co-Author-Berechtigungen (ADR 0053) ─────────────────────

/// <summary>Art des Schreibzugriffs auf ein Modul. Direkt (Tabelle <c>module_coauthors</c>) und abgeleitet (aus einem Themen-Co-Author) sind getrennte Quellen.</summary>
public enum ModuleAccessKind
{
    None = 0,
    /// <summary>Abgeleitet: Co-Author eines Themas, dessen Entwurf das Modul enthält (nichts gespeichert, bei jedem Zugriff neu berechnet).</summary>
    ViaTopic = 1,
    /// <summary>Direkter Modul-Co-Author (ausdrückliche Freigabe des Eigentümers oder eines direkten Co-Autors).</summary>
    Direct = 2,
    Owner = 3,
}

public class ModuleTopicRef
{
    public string TopicId { get; set; } = "";
    public string TopicName { get; set; } = "";
}

/// <summary>Ergebnis der zentralen Modul-Schreibentscheidung (<c>TopicService.GetModuleEditAccessAsync</c>).</summary>
public class ModuleEditAccess
{
    public ModuleAccessKind Kind { get; set; }
    /// <summary>Eigentümer des Moduls (Dienste arbeiten unter dessen Eigentum); leer bei <see cref="ModuleAccessKind.None"/>.</summary>
    public string OwnerUserId { get; set; } = "";
    public bool IsDirect { get; set; }
    /// <summary>Themen, aus denen ein abgeleitetes Recht besteht (aktuelle Themenberechtigung und Modulzuordnung).</summary>
    public List<ModuleTopicRef> ViaTopics { get; set; } = new();
    public bool CanEdit => Kind != ModuleAccessKind.None;
}

public class ModuleDerivedAccess
{
    public string TopicId { get; set; } = "";
    public string TopicName { get; set; } = "";
    public string UserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

/// <summary>Zusammenarbeit an einem Modul: direkte Co-Autoren (verwaltbar) und aus Themen abgeleitete Zugriffe (nur Anzeige).</summary>
public class ModuleCollaboration
{
    public string ModuleId { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public string OwnerUserId { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public string OwnerEmail { get; set; } = "";
    public ModuleAccessKind ViewerKind { get; set; }
    /// <summary>Nur Eigentümer und direkte Modul-Co-Autoren verwalten direkte Co-Autoren; ein abgeleitetes Recht genügt nicht.</summary>
    public bool CanManage { get; set; }
    public List<TopicCoAuthorInfo> DirectCoAuthors { get; set; } = new();
    public List<ModuleDerivedAccess> DerivedAccess { get; set; } = new();
    public List<ModuleTopicRef> ViewerViaTopics { get; set; } = new();
}

/// <summary>Eintrag der Übersicht „Gemeinsam bearbeitet“ (Module): nur Module mit aktuell gültigem Schreibzugriff, nie die übrige Bibliothek des Eigentümers.</summary>
public class CoEditedModule
{
    public ModuleInfo Module { get; set; } = new();
    public string OwnerName { get; set; } = "";
    public bool Direct { get; set; }
    public List<ModuleTopicRef> ViaTopics { get; set; } = new();
}
