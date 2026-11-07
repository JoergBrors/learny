using LearnCards.Web.Data;
using LearnCards.Web.Domain;

namespace LearnCards.Web.Services;

/// <summary>
/// Lernprofile im Zugriffsentscheid (ADR 0049). <b>Bestandsprofil</b> (kein Profileintrag, keine externe Identität): die bisherige Logik von
/// <see cref="ResolveModuleAccessAsync"/> gilt unverändert. <b>Externe Lernprofile</b> (B2B_LEARNER, B2C_LEARNER — oder jede Identität der Art „external“, auch ohne Profileintrag,
/// fail-closed) erhalten Zugriff ausschließlich auf <b>ausdrücklich zugewiesene</b> Inhalte: „global“ und „freigegeben“ bedeuten nicht „erlaubt“, eigene Module gibt es nicht.
/// </summary>
public partial class CardRepository
{
    /// <summary>Das externe Lernprofil des Benutzers (B2B_LEARNER | B2C_LEARNER) oder null (Bestandsprofil).</summary>
    public async Task<string?> GetLearnerProfileAsync(string userId)
    {
        if (!UserService.IsGuid(userId)) return null;
        var rows = await _db.QueryAsync("SELECT profile FROM user_profiles WHERE user_id = @u", new[] { ("u", (object?)userId) });
        return rows.FirstOrDefault()?["profile"];
    }

    /// <summary>
    /// true = eingeschränkt (externes Lernprofil oder externe Identität ohne gültiges Profil): nur ausdrückliche Zuweisungen. Eine externe Identität ohne Profileintrag
    /// erhält nie die Bestandsberechtigung. Ungültige Benutzer-IDs gelten als eingeschränkt (kein Zugriff).
    /// </summary>
    public async Task<bool> IsRestrictedLearnerAsync(string userId)
    {
        if (!UserService.IsGuid(userId)) return true;
        var rows = await _db.QueryAsync("""
            SELECT (SELECT COUNT(*) FROM user_profiles WHERE user_id = @u) AS p,
                   (SELECT COUNT(*) FROM user_identities WHERE user_id = @u AND kind = 'external') AS e
            """, new[] { ("u", (object?)userId) });
        return DbValue.ToInt(rows[0]["p"]) > 0 || DbValue.ToInt(rows[0]["e"]) > 0;
    }

    /// <summary>Besteht eine wirksame (nicht widerrufene) Zuweisung für dieses Modul? Globale Module folgen der Veröffentlichung (<c>publication_id</c>), nicht einer einzelnen Version.</summary>
    public async Task<bool> HasModuleGrantAsync(string userId, ModuleInfo module)
    {
        if (!UserService.IsGuid(userId) || !UserService.IsGuid(module.Id)) return false;
        var rows = await _db.QueryAsync("""
            SELECT 1 FROM module_grants g
            WHERE g.user_id = @u AND g.revoked_at IS NULL
              AND (g.module_id = @m OR (g.publication_id IS NOT NULL AND g.publication_id = @pub))
            LIMIT 1
            """, new (string, object?)[] { ("u", userId), ("m", module.Id), ("pub", string.IsNullOrEmpty(module.PublicationId) ? null : module.PublicationId) });
        return rows.Count > 0;
    }

    private async Task<ModuleAccess?> ResolveRestrictedAccessAsync(string userId, ModuleInfo module)
    {
        if (module.Scope == "private")
        {
            // Keine eigenen Module für externe Profile; ein von einem Eigentümer geteiltes Modul nur zusammen mit einer ausdrücklichen Zuweisung.
            if (string.Equals(module.OwnerUserId, userId, StringComparison.OrdinalIgnoreCase)) return null;
            var shared = await _db.QueryAsync("SELECT 1 FROM module_shares WHERE module_id = @m AND recipient_user_id = @u AND revoked_at IS NULL", new (string, object?)[] { ("m", module.Id), ("u", userId) });
            if (shared.Count == 0 || !await HasModuleGrantAsync(userId, module)) return null;
            module.Context = ContextShared;
            module.OwnerName = await DisplayNameAsync(module.OwnerUserId);
            return new ModuleAccess(module, ContextShared, CanEdit: false);
        }
        if (module.Scope == "global" && module.GlobalStatus == "active")
        {
            if (!await HasModuleGrantAsync(userId, module)) return null;   // global ist nicht „erlaubt“
            module.Context = ContextGlobal;
            module.OwnerName = module.SourceOwnerName ?? "";
            return new ModuleAccess(module, ContextGlobal, CanEdit: false);
        }
        if (module.Scope == ScopeTopic && await CanLearnTopicModuleRestrictedAsync(userId, module.Id))
        {
            module.Context = ContextTopic;
            return new ModuleAccess(module, ContextTopic, CanEdit: false);
        }
        return null;
    }

    /// <summary>Eingefrorene Themenmodul-Kopie: nur über eine ausdrückliche, aktive Themenzuweisung (an die zugewiesene Version) — nie über Eigentum, Freigabe oder globale Sichtbarkeit.</summary>
    private async Task<bool> CanLearnTopicModuleRestrictedAsync(string userId, string moduleId)
    {
        var rows = await _db.QueryAsync("""
            SELECT 1
            FROM topic_version_modules vm
            JOIN topic_versions v ON v.id = vm.version_id
            JOIN topics t ON t.id = v.topic_id
            JOIN topic_assignments a ON a.topic_id = t.id AND a.user_id = @u AND a.status = 'active' AND (a.topic_version IS NULL OR a.topic_version = v.version_number)
            WHERE vm.module_id = @m AND v.status = 'published'
            LIMIT 1
            """, new (string, object?)[] { ("m", moduleId), ("u", userId) });
        return rows.Count > 0;
    }

    /// <summary>Aktive globale Module, die dem Benutzer sichtbar sind: Bestandsprofil alle, externe Profile nur ausdrücklich zugewiesene (keine Metadaten nicht zugewiesener Module).</summary>
    public async Task<List<ModuleInfo>> ListGlobalModulesAsync(string userId)
    {
        var all = await ListGlobalModulesAsync();
        if (!await IsRestrictedLearnerAsync(userId)) return all;
        var visible = new List<ModuleInfo>();
        foreach (var m in all) if (await HasModuleGrantAsync(userId, m)) visible.Add(m);
        return visible;
    }
}
