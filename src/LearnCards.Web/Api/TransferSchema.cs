using System.Text.Json.Nodes;

namespace LearnCards.Web.Api;

/// <summary>
/// Maschinenlesbare Schemas der portablen Formate (ADR 0028): <c>learny-module</c> (vollständiges Modul) und <c>learny-topic</c> (Thema/Lernpfad).
/// Das kanonische Karten-JSON (<see cref="CardImportSchema"/>) bleibt unverändert und wird in <c>cards</c> wiederverwendet.
/// </summary>
public static class TransferSchema
{
    private static JsonObject Str(string desc, params string[] enums)
    {
        var o = new JsonObject { ["type"] = "string", ["description"] = desc };
        if (enums.Length > 0) o["enum"] = new JsonArray(enums.Select(e => (JsonNode)e).ToArray());
        return o;
    }

    private static JsonObject Int(string desc, int? min = null, int? max = null)
    {
        var o = new JsonObject { ["type"] = "integer", ["description"] = desc };
        if (min is not null) o["minimum"] = min;
        if (max is not null) o["maximum"] = max;
        return o;
    }

    private static JsonObject Bool(string desc) => new() { ["type"] = "boolean", ["description"] = desc };
    private static JsonObject Arr(JsonNode items, string desc) => new() { ["type"] = "array", ["description"] = desc, ["items"] = items };

    public static JsonObject ModuleDocument() => new()
    {
        ["name"] = "learny-module",
        ["schema_version"] = 1,
        ["description"] = "Portable, vollständige Moduldefinition. Import erzeugt IMMER ein neues privates Modul des angemeldeten Benutzers (nie ein Überschreiben); bei Namensgleichheit entsteht 'Name (Import)'. IDs, Besitzer, Freigaben, Veröffentlichungsstatus, Lernstände und Ergebnisse sind nicht Teil des Formats.",
        ["rules"] = new JsonArray(
            "format must be \"learny-module\" and schema_version must be 1; unknown top-level fields are rejected.",
            "cards[] uses the canonical card JSON (see /api/schema/import/cards, card_schema). The card field 'module' is ignored (cards always belong to this module); 'id' is ignored (new IDs are assigned).",
            "Images are part of the cards: images[] with mime_type, data_base64 (standard Base64, no data: prefix), alt_text, caption, sort_order. Never invent image data.",
            "Fixed quiz questions of a card live in the card's quiz[] (single source of truth). pool_questions[] only holds additional editor/AI questions stored in the module question pool.",
            "quiz_config is the module quiz configuration; omit it to use the defaults."),
        ["module_schema"] = ModuleSchema(),
        ["example"] = ModuleExample(),
    };

    public static JsonObject ModuleSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("format", "schema_version", "name", "cards"),
        ["properties"] = new JsonObject
        {
            ["format"] = new JsonObject { ["const"] = "learny-module" },
            ["schema_version"] = new JsonObject { ["const"] = 1 },
            ["reference_id"] = Str("Stabile Modulreferenz (GUID). Wird beim Export vergeben, überlebt Umbenennen und wird in Themen-JSON als module_ref verwendet. Optional im Import (wird nur übernommen, wenn der Benutzer sie noch nicht verwendet)."),
            ["name"] = Str("Anzeigename des Moduls, 1–120 Zeichen."),
            ["description"] = Str("Beschreibung, höchstens 2000 Zeichen."),
            ["icon"] = Str("Emoji/Symbol, Standard 📚."),
            ["color"] = Str("Farbe als #RRGGBB, Standard #2AA79B."),
            ["cards"] = Arr(new JsonObject { ["$ref"] = "/api/schema/import/cards#card_schema", ["description"] = "Kanonisches Karten-JSON" }, "Karten in Reihenfolge (höchstens 2000)."),
            ["learning_objectives"] = Arr(new JsonObject { ["type"] = "object", ["required"] = new JsonArray("id", "text"), ["properties"] = new JsonObject { ["id"] = Str("Kleinbuchstaben, Ziffern, . _ - (höchstens 64 Zeichen), eindeutig."), ["text"] = Str("Lernziel, höchstens 500 Zeichen.") } }, "Optional (ADR 0031): Lernziele des Moduls (höchstens 50)."),
            ["competencies"] = Arr(new JsonObject
            {
                ["type"] = "object", ["required"] = new JsonArray("id", "name"),
                ["properties"] = new JsonObject
                {
                    ["id"] = Str("Eindeutige Kompetenz-ID (Kleinbuchstaben, Ziffern, . _ -); Karten verweisen darauf (competencies[])."), ["objective_id"] = Str("Optional: ID eines Lernziels dieses Moduls."),
                    ["name"] = Str("Bezeichnung, höchstens 120 Zeichen."), ["description"] = Str("Beschreibung, höchstens 500 Zeichen."),
                    ["pass_percent"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100, ["default"] = 60, ["description"] = "Bestehensgrenze der Kompetenz (Anteil der Punkte ihrer Fragen)." },
                    ["min_questions"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 50, ["default"] = 1, ["description"] = "Erforderliche Prüfungsabdeckung: Mindestanzahl geprüfter Fragen dieser Kompetenz." },
                },
            }, "Optional (ADR 0031): Kompetenzen des Moduls (höchstens 100). Nachgewiesen gilt eine Kompetenz nur bei ausreichender Prüfung im verifizierten Modulquiz."),
            ["quiz_config"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "Quizkonfiguration des Moduls (siehe GET /api/modules/{id}/quiz-config): enabled, allow_multiple_choice, single_choice, multiple_response, free_text, origin (fixed|ai|mixed), strategy (none|on_demand|prefer_stored|mix), scope (card|module|both), question_count 1–20, pass_percent 1–100, allow_retry, shuffle_questions, shuffle_answers, show_correct_answers.",
            },
            ["pool_questions"] = Arr(new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("type", "question", "explanation"),
                ["properties"] = new JsonObject
                {
                    ["card_index"] = Int("0-basierte Position der Karte in cards[]; fehlt = modulweite Frage.", 0),
                    ["type"] = Str("Fragetyp.", "single_choice", "multiple_response", "free_text"),
                    ["question"] = Str("Fragetext."),
                    ["options"] = Arr(new JsonObject { ["type"] = "string" }, "single_choice: genau 4; multiple_response: 3–6; free_text: keine."),
                    ["correct_indexes"] = Arr(new JsonObject { ["type"] = "integer" }, "0-basierte Indizes der richtigen Optionen (free_text: leer)."),
                    ["explanation"] = Str("Erklärung bzw. Musterlösung."),
                    ["origin"] = Str("manual oder ai.", "manual", "ai"),
                    ["active"] = Bool("Frage aktiv (Standard true)."),
                },
            }, "Zusätzliche Editor-/KI-Fragen des Fragenpools (höchstens 5000)."),
        },
    };

    public static JsonObject ModuleExample() => new()
    {
        ["format"] = "learny-module",
        ["schema_version"] = 1,
        ["reference_id"] = "3f9a1c52-8d47-4e6b-b2a0-5c1d7e9f4a68",
        ["name"] = "Microsoft Entra ID",
        ["description"] = "Identität und Zugriff",
        ["icon"] = "🔐",
        ["color"] = "#2AA79B",
        ["cards"] = new JsonArray(new JsonObject
        {
            ["category"] = "Grundlagen",
            ["term"] = "Bedingter Zugriff",
            ["question"] = "Was steuert Bedingter Zugriff?",
            ["definition"] = "Richtlinien, die Zugriff an **Bedingungen** knüpfen.",
            ["sort_order"] = 1,
            ["quiz"] = new JsonArray(new JsonObject
            {
                ["type"] = "single_choice",
                ["question"] = "Wofür steht Bedingter Zugriff?",
                ["options"] = new JsonArray("Richtlinien für Zugriffsentscheidungen", "Ein Backup-Dienst", "Ein Druckertreiber", "Eine Lizenzart"),
                ["correct_index"] = 0,
                ["explanation"] = "Bedingter Zugriff wertet Signale aus und erzwingt Anforderungen.",
            }),
        }),
        ["quiz_config"] = new JsonObject { ["enabled"] = true, ["question_count"] = 5, ["pass_percent"] = 60 },
        ["pool_questions"] = new JsonArray(),
    };

    public static JsonObject TopicDocument() => new()
    {
        ["name"] = "learny-topic",
        ["schema_version"] = 1,
        ["description"] = "Portable Themendefinition (Lernpfad). Import/Update wirkt ausschließlich auf den ENTWURF eines eigenen Themas und veröffentlicht nie; neue Themenversionen entstehen nur durch die ausdrückliche Veröffentlichung. IDs, Besitzer, Versionen, Status, Freigaben, Zuweisungen, Prüfungsergebnisse und Administratorentscheidungen sind nicht Teil des Formats.",
        ["rules"] = new JsonArray(
            "format must be \"learny-topic\" and schema_version must be 1; unknown fields are rejected.",
            "modules[].module_ref (GUID = reference_id of a module, see export_module / export_topic) is resolved against the caller's OWN private modules and survives renaming; module_name is the display name. Without module_ref the exact module_name (case-insensitive) is used. A module_ref that does not exist, or a global/shared module, is rejected with an error — clone global modules into your own modules first (clone_global_module).",
            "modules[].module may embed a complete learny-module definition. If you already own a module with its reference_id (module_ref) that module is REUSED (nothing is created or overwritten); otherwise a NEW private module is created (name 'X (Import)' if X exists). Without module_ref, module.name must equal module_name.",
            "The order is given by sort_order (ascending, missing = list order).",
            "modules[].exam configures the module exam; exam_config.final_exam the final exam. For final_exam.distribution = \"manual\" set modules[].final_exam_questions per module.",
            "topic_questions[] are the topic's own final-exam questions (single_choice: 4 options, multiple_response: 3–6, free_text: none).",
            "update_topic replaces the whole draft (name, description, modules, exams, topic questions) with the definition; omitted modules are removed from the draft."),
        ["topic_schema"] = TopicSchema(),
        ["example"] = TopicExample(),
    };

    public static JsonObject TopicSchema()
    {
        var moduleExam = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["enabled"] = Bool("Modulprüfung aktiv."),
                ["question_count"] = Int("Fragen je Prüfung.", 1, 20),
                ["single_choice"] = Bool("Single-Choice erlaubt."),
                ["multiple_response"] = Bool("Multiple-Response erlaubt."),
                ["free_text"] = Bool("Freitext erlaubt."),
                ["pass_percent"] = Int("Bestehensgrenze in Prozent.", 1, 100),
                ["allow_retry"] = Bool("Wiederholung erlaubt."),
                ["shuffle_questions"] = Bool("Fragen mischen."),
                ["shuffle_answers"] = Bool("Antworten mischen."),
                ["show_explanations"] = Bool("Erklärungen nach Abgabe zeigen."),
                ["min_percent_for_next"] = Int("Mindestleistung (Prozent), um das nächste Modul freizuschalten; 0 = keine Voraussetzung.", 0, 100),
            },
        };
        var finalExam = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["enabled"] = Bool("Abschlussprüfung aktiv."),
                ["question_count"] = Int("Fragen der Abschlussprüfung.", 1, 50),
                ["pass_percent"] = Int("Bestehensgrenze in Prozent.", 1, 100),
                ["allow_retry"] = Bool("Wiederholung erlaubt."),
                ["random_selection"] = Bool("Zufällige Auswahl."),
                ["shuffle_answers"] = Bool("Antworten mischen."),
                ["single_choice"] = Bool("Single-Choice erlaubt."),
                ["multiple_response"] = Bool("Multiple-Response erlaubt."),
                ["free_text"] = Bool("Freitext erlaubt."),
                ["distribution"] = Str("Verteilung der Fragen über die Module.", "even", "proportional", "manual"),
                ["include_topic_questions"] = Bool("Eigene Themenfragen in den Pool aufnehmen."),
                ["unlock"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["require_all_modules_completed"] = Bool("Alle Module abgeschlossen."),
                        ["require_module_exams_passed"] = Bool("Alle Modulprüfungen bestanden."),
                    },
                },
                ["show_explanations"] = Bool("Erklärungen nach Abgabe zeigen."),
            },
        };
        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("format", "schema_version", "name", "modules"),
            ["properties"] = new JsonObject
            {
                ["format"] = new JsonObject { ["const"] = "learny-topic" },
                ["schema_version"] = new JsonObject { ["const"] = 1 },
                ["name"] = Str("Themenname, 1–120 Zeichen, je Eigentümer eindeutig."),
                ["description"] = Str("Beschreibung, höchstens 2000 Zeichen."),
                ["modules"] = Arr(new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("module_name"),
                    ["properties"] = new JsonObject
                    {
                        ["module_ref"] = Str("Stabile Referenz (GUID, reference_id) eines eigenen privaten Moduls — hat Vorrang vor dem Namen und überlebt Umbenennen."),
                        ["module_name"] = Str("Anzeigename des Moduls; ohne module_ref zugleich Auflösungsschlüssel (Name eines eigenen privaten Moduls)."),
                        ["sort_order"] = Int("Reihenfolge (aufsteigend)."),
                        ["exam"] = moduleExam,
                        ["final_exam_questions"] = Int("Nur bei distribution=manual: Fragen aus diesem Modul.", 0, 50),
                        ["module"] = new JsonObject { ["$ref"] = "/api/schema/import/module#module_schema", ["description"] = "Optional: eingebettete vollständige Moduldefinition (wird als neues eigenes Modul angelegt)." },
                    },
                }, "Module des Lernpfads (höchstens 100)."),
                ["exam_config"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JsonObject { ["final_exam"] = finalExam },
                },
                ["topic_questions"] = Arr(new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("type", "question", "explanation"),
                    ["properties"] = new JsonObject
                    {
                        ["type"] = Str("Fragetyp.", "single_choice", "multiple_response", "free_text"),
                        ["question"] = Str("Fragetext."),
                        ["options"] = Arr(new JsonObject { ["type"] = "string" }, "Antwortoptionen."),
                        ["correct_indexes"] = Arr(new JsonObject { ["type"] = "integer" }, "Richtige Optionen (0-basiert)."),
                        ["explanation"] = Str("Erklärung bzw. Musterlösung."),
                        ["active"] = Bool("Frage aktiv (Standard true)."),
                    },
                }, "Eigene Themenfragen der Abschlussprüfung (höchstens 500)."),
            },
        };
    }

    public static JsonObject TopicExample() => new()
    {
        ["format"] = "learny-topic",
        ["schema_version"] = 1,
        ["name"] = "Microsoft 365 Administration",
        ["description"] = "Lernpfad für Administratoren",
        ["modules"] = new JsonArray(
            new JsonObject { ["module_ref"] = "3f9a1c52-8d47-4e6b-b2a0-5c1d7e9f4a68", ["module_name"] = "Microsoft Entra ID", ["sort_order"] = 1, ["exam"] = new JsonObject { ["enabled"] = true, ["question_count"] = 5, ["pass_percent"] = 60, ["min_percent_for_next"] = 0 } },
            new JsonObject { ["module_name"] = "Exchange Online", ["sort_order"] = 2, ["exam"] = new JsonObject { ["enabled"] = true, ["question_count"] = 5, ["pass_percent"] = 60 } }),
        ["exam_config"] = new JsonObject
        {
            ["final_exam"] = new JsonObject
            {
                ["enabled"] = true, ["question_count"] = 10, ["pass_percent"] = 70, ["distribution"] = "even", ["include_topic_questions"] = true,
                ["unlock"] = new JsonObject { ["require_all_modules_completed"] = false, ["require_module_exams_passed"] = true },
            },
        },
        ["topic_questions"] = new JsonArray(new JsonObject
        {
            ["type"] = "single_choice",
            ["question"] = "Welches Portal verwaltet Exchange Online?",
            ["options"] = new JsonArray("Exchange Admin Center", "Paint", "Notepad", "Taskleiste"),
            ["correct_indexes"] = new JsonArray(0),
            ["explanation"] = "Das Exchange Admin Center ist die Verwaltungsoberfläche.",
        }),
    };
}
