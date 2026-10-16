#:package JsonSchema.Net@9.4.0
#:property PublishAot=false

// Roadmap tool of benjo-code/project-template.
//   dotnet run tools/roadmap.cs -- generate [Docs/roadmap.json]   writes Docs/ROADMAP.md
//   dotnet run tools/roadmap.cs -- check    [Docs/roadmap.json]   validates and compares, writes nothing
//   dotnet run tools/roadmap.cs -- selftest [tools/roadmap-fixtures]
// Run from the repository root. The schema is Docs/roadmap.schema.json next to the source file.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Json.Schema;

return Cli.Run(args);

static class Cli
{
    private const string DefaultSource = "Docs/roadmap.json";
    private const string DefaultFixtures = "tools/roadmap-fixtures";
    private const string SchemaFile = "roadmap.schema.json";
    private const string MarkdownFile = "ROADMAP.md";

    public static int Run(string[] args)
    {
        var path = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        var update = args.Contains("--update");
        switch (args.Length > 0 ? args[0] : string.Empty)
        {
            case "generate":
                return Generate(path ?? DefaultSource);
            case "check":
                return Check(path ?? DefaultSource);
            case "selftest":
                return SelfTest(path ?? DefaultFixtures, update);
            default:
                Console.Error.WriteLine("Aufruf: dotnet run tools/roadmap.cs -- <generate|check|selftest [--update]> [Pfad]");
                return 2;
        }
    }

    private static string SchemaPathFor(string source) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, SchemaFile);

    private static string MarkdownPathFor(string source) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, MarkdownFile);

    private static int Generate(string source)
    {
        var result = Roadmaps.Build(File.ReadAllText(source), File.ReadAllText(SchemaPathFor(source)));
        if (result.Errors.Count > 0)
        {
            return Fail(source, result.Errors);
        }

        File.WriteAllText(MarkdownPathFor(source), result.Markdown, new UTF8Encoding(false));
        Console.WriteLine($"{MarkdownPathFor(source)} erzeugt.");
        return 0;
    }

    private static int Check(string source)
    {
        var result = Roadmaps.Build(File.ReadAllText(source), File.ReadAllText(SchemaPathFor(source)));
        if (result.Errors.Count > 0)
        {
            return Fail(source, result.Errors);
        }

        var target = MarkdownPathFor(source);
        if (!File.Exists(target) || File.ReadAllText(target).ReplaceLineEndings("\n") != result.Markdown)
        {
            Console.Error.WriteLine($"{target} ist nicht aktuell. Bitte `dotnet run tools/roadmap.cs -- generate` ausführen.");
            return 1;
        }

        Console.WriteLine("Roadmap ist gültig und ROADMAP.md ist aktuell.");
        return 0;
    }

    private static int Fail(string source, IReadOnlyList<string> errors)
    {
        Console.Error.WriteLine($"{source} ist ungültig:");
        foreach (var error in errors)
        {
            Console.Error.WriteLine($"  {error}");
        }

        return 1;
    }

    private static int SelfTest(string fixtures, bool update)
    {
        var schema = File.ReadAllText(Path.Combine("Docs", SchemaFile));
        var failures = new List<string>();
        var count = 0;

        foreach (var file in Directory.GetFiles(Path.Combine(fixtures, "valid"), "*.json").Order(StringComparer.Ordinal))
        {
            count++;
            var name = Path.GetFileName(file);
            var json = File.ReadAllText(file);
            var first = Roadmaps.Build(json, schema);
            if (first.Errors.Count > 0)
            {
                failures.Add($"valid/{name}: unerwartet ungültig: {string.Join(" | ", first.Errors)}");
                continue;
            }

            if (Roadmaps.Build(json, schema).Markdown != first.Markdown)
            {
                failures.Add($"valid/{name}: zwei Läufe liefern verschiedene Ausgaben");
            }

            var golden = Path.ChangeExtension(file, ".expected.md");
            if (update)
            {
                File.WriteAllText(golden, first.Markdown, new UTF8Encoding(false));
            }

            if (!File.Exists(golden) || File.ReadAllText(golden) != first.Markdown)
            {
                failures.Add($"valid/{name}: weicht von {Path.GetFileName(golden)} ab");
            }
        }

        foreach (var file in Directory.GetFiles(Path.Combine(fixtures, "invalid"), "*.json").Order(StringComparer.Ordinal))
        {
            count++;
            var name = Path.GetFileName(file);
            var result = Roadmaps.Build(File.ReadAllText(file), schema);
            if (result.Errors.Count == 0)
            {
                failures.Add($"invalid/{name}: unerwartet gültig");
                continue;
            }

            var all = string.Join('\n', result.Errors);
            var expectedFile = Path.ChangeExtension(file, ".expected.txt");
            // A line is a substring the message must contain; a line starting with "!" one it must not contain.
            foreach (var line in File.ReadAllLines(expectedFile).Where(l => l.Length > 0))
            {
                var forbidden = line.StartsWith('!');
                var part = forbidden ? line[1..] : line;
                if (all.Contains(part, StringComparison.Ordinal) == forbidden)
                {
                    failures.Add($"invalid/{name}: Meldung {(forbidden ? "enthält" : "enthält nicht")} „{part}“:\n    {all.Replace("\n", "\n    ")}");
                }
            }
        }

        foreach (var failure in failures)
        {
            Console.Error.WriteLine(failure);
        }

        Console.WriteLine(failures.Count == 0 ? $"selftest: {count} Fixtures bestanden." : $"selftest: {failures.Count} Fehler.");
        return failures.Count == 0 ? 0 : 1;
    }
}

sealed record BuildResult(string Markdown, List<string> Errors);

static class Roadmaps
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Keywords whose messages only summarize the findings below them.
    private static readonly HashSet<string> Aggregates = ["properties", "items", "additionalProperties", "allOf", "if", "then", "else", "$ref"];

    public static BuildResult Build(string json, string schemaJson)
    {
        var errors = new List<string>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException exception)
        {
            return new BuildResult(string.Empty, [$"Kein gültiges JSON: {exception.Message}"]);
        }

        using (document)
        {
            var schema = JsonSchema.FromText(schemaJson);
            var evaluation = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (!evaluation.IsValid)
            {
                foreach (var detail in (evaluation.Details ?? []).Where(d => d.Errors is { Count: > 0 } && !d.EvaluationPath.ToString().Contains("/if/", StringComparison.Ordinal)))
                {
                    foreach (var error in detail.Errors!.Where(e => !Aggregates.Contains(e.Key)))
                    {
                        var message = error.Key.Length == 0 ? "Feld nicht erlaubt" : error.Value;
                        errors.Add($"{Describe(detail.InstanceLocation.ToString())}: {message}");
                    }
                }

                return new BuildResult(string.Empty, [.. errors.Distinct()]);
            }

            var roadmap = document.RootElement.Deserialize<Roadmap>(Options)!;
            errors.AddRange(Check(roadmap));
            return errors.Count > 0 ? new BuildResult(string.Empty, errors) : new BuildResult(Markdown.Render(roadmap), errors);
        }
    }

    /// <summary>Turns a JSON pointer such as /subprojects/0/milestones/1/id into subprojects[0].milestones[1].id.</summary>
    private static string Describe(string pointer)
    {
        if (pointer.Length == 0)
        {
            return "(Datei)";
        }

        var text = new StringBuilder();
        foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                text.Append('[').Append(index).Append(']');
            }
            else
            {
                text.Append(text.Length == 0 ? string.Empty : ".").Append(name);
            }
        }

        return text.ToString();
    }

    /// <summary>The rules a JSON schema cannot express: references between fields and real dates.</summary>
    private static List<string> Check(Roadmap roadmap)
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (var s = 0; s < roadmap.Subprojects.Count; s++)
        {
            var subproject = roadmap.Subprojects[s];
            if (s > 0 && subproject.Number <= roadmap.Subprojects[s - 1].Number)
            {
                errors.Add($"subprojects[{s}].number: Die Nummer {subproject.Number} muss größer sein als die des Teilprojekts davor");
            }

            for (var m = 0; m < subproject.Milestones.Count; m++)
            {
                var milestone = subproject.Milestones[m];
                if (!ids.Add(milestone.Id))
                {
                    errors.Add($"subprojects[{s}].milestones[{m}].id: Die Id „{milestone.Id}“ kommt mehrfach vor");
                }
            }
        }

        for (var s = 0; s < roadmap.Subprojects.Count; s++)
        {
            for (var m = 0; m < roadmap.Subprojects[s].Milestones.Count; m++)
            {
                var milestone = roadmap.Subprojects[s].Milestones[m];
                var at = $"subprojects[{s}].milestones[{m}]";
                CheckDate(errors, $"{at}.completedOn", milestone.CompletedOn);
                for (var p = 0; p < (milestone.Prerequisites?.Count ?? 0); p++)
                {
                    var id = milestone.Prerequisites![p];
                    if (!ids.Contains(id))
                    {
                        errors.Add($"{at}.prerequisites[{p}]: Einen Milestone „{id}“ gibt es nicht");
                    }
                    else if (id == milestone.Id)
                    {
                        errors.Add($"{at}.prerequisites[{p}]: Ein Milestone kann nicht Voraussetzung von sich selbst sein");
                    }
                }
            }
        }

        var versions = new HashSet<string>(StringComparer.Ordinal);
        var releases = roadmap.Releases ?? [];
        for (var r = 0; r < releases.Count; r++)
        {
            var release = releases[r];
            var at = $"releases[{r}]";
            if (!versions.Add(release.Version))
            {
                errors.Add($"{at}.version: Die Version „{release.Version}“ kommt mehrfach vor");
            }

            CheckDate(errors, $"{at}.date", release.Date);
            if (!ids.Contains(release.AfterMilestone))
            {
                errors.Add($"{at}.afterMilestone: Einen Milestone „{release.AfterMilestone}“ gibt es nicht");
            }

            for (var c = 0; c < (release.Covers?.Count ?? 0); c++)
            {
                if (!ids.Contains(release.Covers![c]))
                {
                    errors.Add($"{at}.covers[{c}]: Einen Milestone „{release.Covers[c]}“ gibt es nicht");
                }
            }
        }

        var changes = roadmap.ScopeChanges ?? [];
        for (var i = 0; i < changes.Count; i++)
        {
            CheckDate(errors, $"scopeChanges[{i}].date", changes[i].Date);
        }

        return errors;
    }

    private static void CheckDate(List<string> errors, string at, string? value)
    {
        if (value is not null && !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            errors.Add($"{at}: „{value}“ ist kein gültiges Datum (JJJJ-MM-TT)");
        }
    }
}

static class Markdown
{
    private const string Intro = """
        Gesamtfahrplan für das Projekt. Jeder Milestone ist so geschnitten, dass er in **einem** Plan und einer Umsetzungsrunde abgearbeitet werden kann.

        > **Diese Datei ist generiert.** Quelle ist [roadmap.json](roadmap.json) (Schema: [roadmap.schema.json](roadmap.schema.json)). Änderungen nur dort, danach `dotnet run tools/roadmap.cs -- generate`.

        ## Arbeitsweise

        1. **„Plane Milestone N“:** Claude Code liest diese Roadmap, die verlinkte Spec und den aktuellen Code-Stand und schreibt einen Plan nach `Docs/superpowers/plans/YYYY-MM-DD-milestone-NN-<thema>.md`. Der Plan wird beim Milestone verlinkt, Status → `🟡 geplant`.
        2. **Umsetzung:** Plan abarbeiten. Jede erledigte Aufgabe wird sofort abgehakt (`done: true` in `Docs/roadmap.json`).
        3. **Abschluss:** Alle Abnahmekriterien geprüft und abgehakt → Status `✅ fertig`, Datum eintragen.
        4. Scope-Änderungen während der Umsetzung werden unter „Scope-Änderungen“ festgehalten, nicht stillschweigend im Code.

        **Status-Legende:** `⬜ offen` · `🟡 geplant` · `🔵 in Arbeit` · `✅ fertig`

        **Regeln:**
        - Milestones grundsätzlich in Reihenfolge abarbeiten. Maßgeblich sind die Voraussetzungen beim jeweiligen Milestone.
        - Ein Milestone beginnt mit `Spec erstellen`, wenn für sein Teilprojekt noch keine Spec existiert. Die Spec entsteht per Brainstorming und wird unter `Docs/superpowers/specs/` abgelegt.
        - Aufgaben späterer Teilprojekte sind absichtlich grob gehalten und werden beim Schreiben der jeweiligen Spec präzisiert.
        """;

    public static string Render(Roadmap roadmap)
    {
        var releases = roadmap.Releases ?? [];
        var doc = new Writer();

        void Line(string value = "") => doc.Line(value);

        Line("<!-- Generiert aus Docs/roadmap.json mit `dotnet run tools/roadmap.cs -- generate` – nicht von Hand ändern. -->");
        Line($"# {roadmap.Title} – Roadmap");
        Line();
        Line(Intro.ReplaceLineEndings("\n")); // a raw string literal takes the line endings of this file
        Line();
        Line("## Übersicht");
        Line();
        Line("| # | Milestone | Teilprojekt | Status | Plan | Fertig am |");
        Line("|---|---|---|---|---|---|");
        foreach (var subproject in roadmap.Subprojects)
        {
            foreach (var milestone in subproject.Milestones)
            {
                var plan = milestone.Plan is null ? "–" : $"[Plan]({milestone.Plan})";
                Line($"| {milestone.Id} | {Cell(milestone.Title)} | {subproject.Number} – {Cell(subproject.Name)} | {StatusLabel(milestone.Status)} | {plan} | {milestone.CompletedOn ?? "–"} |");
                foreach (var release in releases.Where(r => r.AfterMilestone == milestone.Id))
                {
                    Line($"| – | **Release `v{release.Version}`** – {Cell(release.Title)}{Covers(release.Covers)} | – | {StatusLabel(release.Status)} | – | {release.Date ?? "–"} |");
                }
            }
        }

        Line();
        Line("## Scope-Änderungen");
        Line();
        var changes = roadmap.ScopeChanges ?? [];
        if (changes.Count == 0)
        {
            Line("_Noch keine._");
        }

        foreach (var change in changes)
        {
            Line($"- **{change.Date} ({change.Context}):** {Item(change.Text)}");
        }

        foreach (var block in roadmap.Blocks ?? [])
        {
            Line();
            Line($"## {block.Title}");
            Line();
            Line(block.Text);
        }

        foreach (var subproject in roadmap.Subprojects)
        {
            Line();
            Line("---");
            Line();
            Line($"# Teilprojekt {subproject.Number} – {subproject.Name}");
            if (subproject.Spec is { } spec)
            {
                Line();
                var link = spec.Link is null ? null : $"[{Path.GetFileName(spec.Link)}]({spec.Link})";
                Line($"**Spec:** {string.Join(' ', new[] { link, spec.Text }.Where(p => p is not null))}");
            }

            foreach (var milestone in subproject.Milestones)
            {
                Line();
                RenderMilestone(milestone, doc);
                foreach (var release in releases.Where(r => r.AfterMilestone == milestone.Id))
                {
                    RenderRelease(release, doc);
                }
            }
        }

        Line();
        Line("## Backlog");
        Line();
        Line("Bewusst nicht eingeplant. Ideen, die `/ideen` hier ablegt, stehen als Liste; bei Bedarf wird daraus ein Milestone.");
        Line();
        var backlog = roadmap.Backlog ?? [];
        if (backlog.Count == 0)
        {
            Line("_Noch nichts._");
        }

        foreach (var entry in backlog)
        {
            Line($"- **{entry.Title}:** {Item(entry.Text)}{(entry.Idea is { } idea ? $" (Idee #{idea})" : string.Empty)}");
        }

        // Text values may carry CR, CRLF, U+2028 or the like: the output has LF only, or `check` could never agree with `generate`.
        return doc.ToString().ReplaceLineEndings("\n").TrimEnd('\n') + "\n";
    }

    private static void RenderMilestone(Milestone milestone, Writer line)
    {
        line.Line($"## Milestone {milestone.Id} – {milestone.Title}");
        var status = milestone.CompletedOn is null ? StatusLabel(milestone.Status) : $"{StatusLabel(milestone.Status)} ({milestone.CompletedOn})";
        var plan = milestone.Plan is null ? "–" : $"[{Path.GetFileName(milestone.Plan)}]({milestone.Plan})";
        var fields = new List<string> { $"**Status:** {status}", $"**Plan:** {plan}" };
        if (milestone.Spec is not null)
        {
            fields.Add($"**Spec:** [{Path.GetFileName(milestone.Spec)}]({milestone.Spec})");
        }

        fields.Add($"**Voraussetzungen:** {Prerequisites(milestone)}");
        line.Line(string.Join(" · ", fields));
        line.Line();
        line.Line($"Ziel: {milestone.Goal}");
        if (milestone.Tasks.Count > 0)
        {
            line.Line();
            line.Line("**Aufgaben**");
            foreach (var task in milestone.Tasks)
            {
                line.Line($"- [{(task.Done ? 'x' : ' ')}] {Item(task.Text)}");
            }
        }

        RenderCriteria("**Abnahmekriterien**", milestone.Acceptance, line);
        RenderBlocks(milestone.Blocks, line);
    }

    private static void RenderRelease(Release release, Writer line)
    {
        var criteria = release.Acceptance ?? [];
        var blocks = release.Blocks ?? [];
        if (release.Intro is null && criteria.Count == 0 && blocks.Count == 0)
        {
            return;
        }

        line.Line();
        line.Line($"## Release `v{release.Version}` – Abnahme");
        if (release.Intro is not null)
        {
            line.Line();
            line.Line(release.Intro);
        }

        RenderCriteria(null, criteria, line);
        RenderBlocks(blocks, line);
    }

    private static void RenderCriteria(string? heading, List<Criterion> criteria, Writer line)
    {
        if (criteria.Count == 0)
        {
            return;
        }

        line.Line();
        if (heading is not null)
        {
            line.Line(heading);
        }

        foreach (var criterion in criteria)
        {
            var evidence = criterion.Evidence is null ? string.Empty : $" – {criterion.Evidence}";
            line.Line($"- [{(criterion.Done ? 'x' : ' ')}] {Item(criterion.Text + evidence)}");
        }
    }

    private static void RenderBlocks(List<Block>? blocks, Writer line)
    {
        foreach (var block in blocks ?? [])
        {
            line.Line();
            line.Line($"**{block.Title}**");
            line.Line(block.Text);
        }
    }

    private static string Prerequisites(Milestone milestone)
    {
        var ids = (milestone.Prerequisites ?? []).Select(id => $"M{id}").ToList();
        var note = milestone.PrerequisitesNote;
        return (ids.Count, note) switch
        {
            (0, null) => "keine",
            (0, _) => note!,
            (_, null) => string.Join(", ", ids),
            _ => $"{string.Join(", ", ids)} ({note})",
        };
    }

    private static string Covers(List<string>? covers) => covers switch
    {
        null or { Count: 0 } => string.Empty,
        { Count: 1 } => $" (M{covers[0]})",
        _ => $" (M{covers[0]} bis M{covers[^1]})",
    };

    private static string StatusLabel(string status) => status switch
    {
        "open" => "⬜ offen",
        "planned" => "🟡 geplant",
        "in-progress" => "🔵 in Arbeit",
        _ => "✅ fertig",
    };

    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);

    /// <summary>Continuation lines of a list item are indented so they stay part of it.</summary>
    private static string Item(string value) => value.ReplaceLineEndings("\n").Replace("\n", "\n  ", StringComparison.Ordinal);
}

sealed record Roadmap(
    int SchemaVersion,
    string Title,
    List<Subproject> Subprojects,
    List<ScopeChange>? ScopeChanges = null,
    List<BacklogItem>? Backlog = null,
    List<Block>? Blocks = null,
    List<Release>? Releases = null);

sealed record Subproject(int Number, string Name, List<Milestone> Milestones, SpecRef? Spec = null);

sealed record SpecRef(string? Text = null, string? Link = null);

sealed record Milestone(
    string Id,
    string Title,
    string Status,
    string Goal,
    List<TaskItem> Tasks,
    List<Criterion> Acceptance,
    string? CompletedOn = null,
    string? Plan = null,
    string? Spec = null,
    List<string>? Prerequisites = null,
    string? PrerequisitesNote = null,
    List<Block>? Blocks = null);

sealed record TaskItem(string Text, bool Done);

sealed record Criterion(string Text, bool Done, string? Evidence = null);

sealed record Block(string Title, string Text);

sealed record ScopeChange(string Date, string Context, string Text);

sealed record BacklogItem(string Title, string Text, int? Idea = null);

sealed record Release(
    string Version,
    string Title,
    string Status,
    string AfterMilestone,
    string? Date = null,
    List<string>? Covers = null,
    string? Intro = null,
    List<Criterion>? Acceptance = null,
    List<Block>? Blocks = null);

sealed class Writer
{
    private readonly StringBuilder _text = new();

    public void Line(string value = "") => _text.Append(value).Append('\n');

    public override string ToString() => _text.ToString();
}
