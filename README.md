# <Projektname>

<Kurzbeschreibung: Was macht das Projekt, für wen?>

**Status:** in Entwicklung – siehe [Docs/ROADMAP.md](Docs/ROADMAP.md)

## Setup

<Voraussetzungen, Build, Start>

## Lizenz

<Lizenz oder „Proprietär – alle Rechte vorbehalten“>

---

## Neues Projekt aus diesem Template

> Diesen Abschnitt nach dem Einrichten löschen.

> [!IMPORTANT]
> Repos immer in der Orga **`benjo-code`** anlegen. Ohne Owner-Präfix (`gh`) bzw. ohne Auswahl „Owner: benjo-code“ (Browser) landet das Repo unter dem persönlichen Account.

Alle Befehle in Git Bash ausführen, `<name>` durch den Repo-Namen ersetzen.

### 1. Repo anlegen und klonen

```bash
gh repo create benjo-code/<name> --template benjo-code/project-template --private --clone
cd <name>
git pull origin main   # falls der Klon leer ist: kurz warten, erneut ausführen
gh repo view benjo-code/<name> --json owner -q .owner.login   # muss "benjo-code" ausgeben
```

### 2. `dev` anlegen, als Default-Branch setzen, Merge-Optionen einstellen

```bash
git switch -c dev
git push -u origin dev
gh repo edit benjo-code/<name> --default-branch dev --enable-merge-commit --enable-squash-merge --enable-rebase-merge=false --delete-branch-on-merge
```

### 3. Branch Protection für `main` und `dev`

PR Pflicht, kein Force-Push, kein Löschen. Admins sind ausgenommen (`enforce_admins: false`); die Regel „nie direkt auf `main`/`dev`“ aus der `CLAUDE.md` gilt trotzdem. Der Check `roadmap` (Workflow `.github/workflows/roadmap.yml`, kommt mit der Vorlage) ist von Anfang an Pflicht; der CI-Check für Build und Test wird in M0 ergänzt, sobald die CI existiert.

```bash
REPO=benjo-code/<name>
for b in main dev; do
gh api -X PUT "repos/$REPO/branches/$b/protection" --input - <<'EOF'
{
  "required_status_checks": { "strict": true, "contexts": ["roadmap"] },
  "enforce_admins": false,
  "required_pull_request_reviews": { "required_approving_review_count": 0 },
  "restrictions": null,
  "allow_force_pushes": false,
  "allow_deletions": false
}
EOF
done
```

Ab jetzt geht nichts mehr direkt auf `main` oder `dev`, alles läuft über Feature-Branches und PRs.

### 4. Loslegen

In Claude Code **„Plane Milestone 0“** sagen. Claude legt dafür den Branch `feature/m00-…` an. Dort werden auch der Kopf der `CLAUDE.md`, diese README und die Platzhalter in `Docs/roadmap.json` (danach `dotnet run tools/roadmap.cs -- generate`) und `Docs/DECISIONS.md` ausgefüllt.

### 5. Roadmap-Werkzeug

Die Roadmap steht in `Docs/roadmap.json`, `Docs/ROADMAP.md` wird mit `dotnet run tools/roadmap.cs -- generate` erzeugt (siehe „Roadmap pflegen“ in der `CLAUDE.md`). Dafür ist das .NET-10-SDK auf dem Entwicklungsrechner nötig, auch wenn das Projekt sonst einen anderen Stack nutzt. Der Job `roadmap` läuft auf Runnern mit dem Label `dotnet-linux` der Orga. Den Ordner `tools/roadmap-fixtures/` braucht ein Projekt nicht, er testet nur die Vorlage.
