# Milestone 1 – PDFium-Anbindung & Render-Kern – Implementierungsplan

> **Für agentische Ausführer:** Pflicht-Skill: `superpowers:subagent-driven-development` (empfohlen) oder `superpowers:executing-plans`. Die Schritte nutzen Checkbox-Syntax (`- [ ]`).

**Ziel:** Der Kern öffnet PDFs (auch passwortgeschützte), rendert Seiten und Kacheln progressiv mit Pause und Abbruch, extrahiert Text, und das ist über die CLI `pagewing info|render|text`, eine Render-Regression auf Windows und Linux und Benchmarks nachweisbar. Dazu kommt die Vergleichsmessung mit Acrobat, Chrome und SumatraPDF, aus der die Startzeit-Budgets endgültig festgelegt werden.

**Architektur:** `Pagewing.Pdfium` kapselt PDFium komplett (internal, sichtbar nur für `Pagewing.Core` und die Tests). Alle Aufrufe laufen auf einem `PdfiumThread` mit zwei Prioritäten und schrittweisen Aufträgen (Grundlage für Pause und Abbruch). `Pagewing.Core` bietet eine PDFium-freie, asynchrone API (`PdfEngine`, `PdfDocument`, `RenderedImage`). Die CLI ruft nur Core. Kacheln werden wie in Chrome über `FPDF_RenderPageBitmap_Start` mit negativem Offset gerendert (die Matrix-Variante ist nicht progressiv, siehe Entscheidungen).

**Tech-Stack:** .NET 10, Native AOT, `bblanchon.PDFium.Win32` und `.Linux` 157.0.8100 (ohne V8), `[LibraryImport]`, xunit.v3 (Microsoft.Testing.Platform), BenchmarkDotNet 0.15.8 (MIT).

**Spec:** [2026-10-16-pagewing-design.md](../specs/2026-10-16-pagewing-design.md), Abschnitte 5.2, 5.3, 5.4, 7. Roadmap: [Milestone 1](../../ROADMAP.md).

## Global Constraints

- .NET 10, Native AOT Pflicht für App und CLI; `IsAotCompatible` je Projekt unter `src/`; AOT- und Trim-Warnungen sind Fehler; `TreatWarningsAsErrors` gilt. Keine Warnungen unterdrücken, keine Tests überspringen.
- „PDFium ist nicht thread-sicher, auch nicht über verschiedene Dokumente hinweg.“ Nur `Pagewing.Pdfium` ruft PDFium auf, ausschließlich auf dem `PdfiumThread`; ein Aufruf von einem anderen Thread löst eine Exception aus. Kein paralleles Rendern.
- Der UI-Thread wartet nie auf PDFium. App und CLI kennen keine PDFium-Typen.
- Nur PDFium-Pakete ohne V8: `bblanchon.PDFium.Win32` und `bblanchon.PDFium.Linux`, nicht `bblanchon.PDFiumV8.*`. Keine PDFiumCore, keine weiteren neuen Abhängigkeiten außer BenchmarkDotNet (Eintrag in `Docs/DECISIONS.md`).
- Bindings nur für Funktionen, die gebraucht werden. Konstanten gegen die Header der ausgelieferten PDFium-Version prüfen (Test in Task 2).
- Keine Netzwerkzugriffe durch Pagewing; keine Telemetrie.
- Test-PDFs nur mit klarer Lizenz, Herkunft und Lizenz je Datei in `testdata/pdfs/LICENSES.md`. Referenzbilder nur bewusst aktualisieren. Budgets nie lockern, nur damit etwas grün wird.
- Testnamen ohne Unterstriche (CA1707). Code und Kommentare Englisch, Doku und Commit-Messages Deutsch, Conventional Commits. Gezielt stagen (`git add <datei>`), nach jedem Commit `git show --stat HEAD` prüfen. Commit-Trailer wie im Repo üblich (`Co-Authored-By`-Zeile laut Session-Vorgabe).
- Nur Milestone-1-Umfang: keine UI, kein Render-Scheduler und kein Kachel-Cache (M3), keine Suche (M5), kein Speichern (M9).
- „Fertig“ heißt: `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx`, `dotnet format Pagewing.slnx --verify-no-changes` und `dotnet run tools/roadmap.cs -- check` nachweislich grün.
- Zu Beginn der Umsetzung den Status von Milestone 1 in `Docs/roadmap.json` auf `in-progress` setzen (Übersichtstabelle und Milestone).
- Nach jedem Task die unter **Roadmap** genannten Aufgaben in `Docs/roadmap.json` abhaken, `dotnet run tools/roadmap.cs -- generate` ausführen und `roadmap.json` samt `ROADMAP.md` mit committen.

## Review Focus

Eingaben und Bedingungen, die die Spec nicht nennt, die aber im Alltag auftreten. Der Test dazu steht jeweils im genannten Task.

1. Datei fehlt, ist ein Verzeichnis, ist leer, ist keine PDF oder wird gelöscht, während sie offen ist: klare `PdfOpenException` statt Absturz; die offene Datei blockiert Löschen und Ersetzen nicht (Task 4).
2. Seitenindex negativ oder zu groß, Zugriff nach `Dispose`: `ArgumentOutOfRangeException` bzw. `ObjectDisposedException`, nie eine Zugriffsverletzung in nativem Code; Seiten schließen vor dem Dokument (Task 4, 5).
3. Absurd große Seiten- oder Kachelgrößen (Maßstab 100000): `PdfRenderException` statt Speicherfehler oder Absturz (Task 5).
4. Viele gleichzeitige Aufträge, Abbruch mitten im Rendern und verschränkte Kacheln derselben Seite: Ergebnisse bleiben bitgleich zum Einzelrendering, der Thread bleibt benutzbar (Task 5).
5. Pfade, Passwörter und Ausgabe mit Nicht-ASCII-Zeichen (Umlaute, CJK) und ungültige Seitenbereiche in der CLI: richtige Dateien, UTF-8-Textausgabe auch auf der Windows-Konsole, Exit-Code 2 mit Hinweis statt Stacktrace (Task 8).

## Entscheidungen dieses Plans (Annahmen sind markiert)

- **Dateizugriff:** `FPDF_LoadCustomDocument` mit `FPDF_FILEACCESS` und Lese-Callback (`RandomAccess.Read`), geöffnet mit `FileShare.ReadWrite | FileShare.Delete`. Das liest lazy (schnell bei 1000 Seiten), kennt keine Pfadkodierungs-Probleme und blockiert Ersetzen nicht (M9 braucht das). *Eigene Entscheidung.*
- **`unsigned long` ist unter Windows 32 Bit, unter Linux 64 Bit.** Alle `unsigned long` der PDFium-API werden mit `CULong` gebunden (`FPDF_GetLastError`, `FPDF_GetMetaText`, `FPDF_FILEACCESS`). Folge: Dateien über 4 GiB lassen sich unter Windows nicht öffnen und liefern `PdfOpenError.CannotRead`.
- **Kacheln:** `FPDF_RenderPageBitmap_Start` mit `start_x = -Kachel.X`, `start_y = -Kachel.Y` und Größe der ganzen Seite in Pixeln auf ein Bitmap in Kachelgröße (so rendert Chrome). `FPDF_RenderPageBitmapWithMatrix` ist nicht progressiv und wird nicht gebunden; das weicht vom Wortlaut der Roadmap („Matrix + Clipping“) und der Spec 5.3 ab und wird in Task 12 in beiden nachgetragen. Der Kacheltest (Task 5) ist die Probe, dass das Ergebnis dem Ganzseiten-Rendering entspricht.
- **Bitmap:** `FPDFBitmap_CreateEx` im Format BGRx über ein gepinntes Managed-Array (`GC.AllocateUninitializedArray<byte>(n, pinned: true)`), weiß gefüllt, ohne Kopie. Das Array wird erst nach `FPDF_RenderPage_Close` und `FPDFBitmap_Destroy` freigegeben.
- **Handles schließen nie von einem fremden Thread:** `ReleaseHandle` auf einem anderen Thread reiht das Schließen per `PdfiumThread.Post` ein. Kind-Handles halten das Eltern-Handle per `DangerousAddRef`, damit das Dokument nie vor seinen Seiten schließt.
- **Ein gemeinsamer Thread je Prozess:** `PdfiumThread.Shared` (ruft `FPDF_InitLibrary` einmal; `FPDF_DestroyLibrary` entfällt beim Prozessende). `PdfEngine` nutzt ihn. Weitere `PdfiumThread`-Instanzen gibt es nur in Tests mit Dummy-Hooks.
- **Fehlertexte der Core-Exceptions sind Englisch** (die UI lokalisiert später anhand von `PdfOpenError`). CLI-Ausgaben sind Englisch und stabil (Skriptnutzung). *Eigene Entscheidung.*
- **Textzeilenumbrüche:** Core liefert `\n` (PDFium liefert `\r\n`). *Eigene Entscheidung.*
- **CLI-Tests** liegen in `Pagewing.Core.Tests/Cli` mit Projektreferenz auf `Pagewing.Cli`, kein neues Testprojekt. *Eigene Entscheidung.*
- **Test-PDFs** werden unkomprimiert von `tools/make-testpdfs.cs` erzeugt (kein Zlib, damit die Bytes über .NET-Versionen stabil bleiben) und eingecheckt; ein `check`-Modus prüft in der CI, dass sie reproduzierbar sind.
- **Referenzbilder:** ein gemeinsamer Satz für beide Betriebssysteme, Maßstab 144 dpi (`--dpi 144`), Pixeltoleranz plus Anteil abweichender Pixel. Schriften in den eigenen Test-PDFs sind die Standard-14-Schriften (PDFium bringt Ersatzschriften mit). Bricht die Toleranz zwischen Windows und Linux nur wegen Text, ist die Ausweichlösung: Text-PDFs mit eingebetteter Schrift (OFL-Subset), Eintrag in `DECISIONS.md` (Task 9).

## Dateistruktur

```
Directory.Packages.props                       + PDFium.Win32/Linux 157.0.8100, BenchmarkDotNet 0.15.8
Pagewing.slnx                                  + Render.Tests, Benchmarks
third-party/pdfium/                            PDFium-Lizenzen (aus dem Release chromium/8100) + README.md
src/Pagewing.Pdfium/                           (alles internal)
  PdfiumThread.cs, SteppedWork.cs, PdfiumPriority.cs, PdfiumThreadViolationException.cs
  Native/Fpdf.cs, Native/FpdfConstants.cs, Native/Handles.cs
  PdfiumDocument.cs, PdfiumPage.cs, PdfiumTextPage.cs, PdfiumRenderWork.cs, PdfiumImage.cs, PdfiumError.cs
src/Pagewing.Core/
  Pdf/PdfEngine.cs, PdfDocument.cs, PdfMetadata.cs, PdfPageSize.cs, PixelRect.cs, RenderedImage.cs, RenderOptions.cs, PdfExceptions.cs
  Imaging/PngEncoder.cs
src/Pagewing.Cli/
  Program.cs, CliApp.cs, CliOptions.cs, PageRange.cs, Commands/{Info,Render,Text}Command.cs
tools/make-testpdfs.cs, tools/measure-open.cs, tools/smoke-cli.cs (erweitert)
testdata/pdfs/*.pdf, testdata/pdfs/LICENSES.md, testdata/references/<name>/*.png
tests/Shared/{TestData.cs,PngDecoder.cs}       per <Compile Link> in beide Testprojekte
tests/Pagewing.Core.Tests/{Pdfium,Pdf,Imaging,Cli}/…
tests/Pagewing.Render.Tests/                   Render-Regression
bench/Pagewing.Benchmarks/, bench/compare/README.md
```

---

### Task 1: PdfiumThread

**Roadmap:** (Teil von „`Pagewing.Pdfium`: … `PdfiumThread`“, abhaken in Task 2)

**Files:**
- Create: `src/Pagewing.Pdfium/PdfiumThread.cs`, `SteppedWork.cs`, `PdfiumPriority.cs`, `PdfiumThreadViolationException.cs`
- Modify: `src/Pagewing.Pdfium/Pagewing.Pdfium.csproj` (`InternalsVisibleTo` für `Pagewing.Core` und `Pagewing.Core.Tests`)
- Test: `tests/Pagewing.Core.Tests/Pdfium/PdfiumThreadTests.cs`

**Interfaces:**
- Produces (alles `internal`, Namespace `Pagewing.Pdfium`):
  - `enum PdfiumPriority { Interactive, Background }`
  - `sealed class PdfiumThreadViolationException : InvalidOperationException`
  - `abstract class SteppedWork<T> : IDisposable { abstract bool Step(); /* true = fertig */ abstract T Complete(); virtual void Dispose() }`
  - `sealed class PdfiumThread : IDisposable`:
    - `PdfiumThread(Action? onStart = null, Action? onStop = null, string name = "Pagewing.Pdfium")`
    - `bool IsCurrent { get; }`, `void VerifyAccess()` (wirft `PdfiumThreadViolationException` auf fremdem Thread)
    - `Task<T> InvokeAsync<T>(Func<T> work, PdfiumPriority priority = PdfiumPriority.Interactive, CancellationToken ct = default)`, Überladung für `Action`
    - `Task<T> InvokeSteppedAsync<T>(Func<SteppedWork<T>> start, PdfiumPriority priority = PdfiumPriority.Background, CancellationToken ct = default)` – `start` und alle `Step`-Aufrufe laufen auf dem Thread
    - `void Post(Action work)` – ohne Rückgabe, Ausnahmen werden verschluckt, nach `Dispose` wirkungslos
    - `Dispose()` – führt `onStop` auf dem Thread aus, wartet auf das Ende; danach wirft `InvokeAsync` `ObjectDisposedException`

- [ ] **Step 1: Tests schreiben** (`PdfiumThreadTests`, alle ohne PDFium, mit `new PdfiumThread()`):
  - `InvokeRunsOnTheDedicatedThread`: `Environment.CurrentManagedThreadId` im Auftrag ≠ Aufrufer, `IsCurrent` ist darin `true`, außerhalb `false`.
  - `AllWorkRunsOnTheSameThread`: zwei Aufträge, gleiche Thread-ID.
  - `VerifyAccessThrowsOnForeignThread`: `Assert.Throws<PdfiumThreadViolationException>(thread.VerifyAccess)` auf dem Testthread, kein Wurf innerhalb eines Auftrags.
  - `ExceptionInWorkFlowsToTheTaskAndThreadSurvives`.
  - `InteractiveWorkRunsBetweenBackgroundSteps`: Hintergrund-Auftrag mit 3 Schritten; Schritt 1 wartet auf ein `ManualResetEventSlim`, das der Test erst nach dem Einreihen eines Interactive-Auftrags setzt; Reihenfolge-Protokoll muss `step1, interactive, step2, step3` sein.
  - `CancellationStopsSteppedWorkAndDisposesIt`: Token nach Schritt 1 abbrechen → Task ist `Canceled`, `Dispose` des Auftrags genau einmal aufgerufen, kein weiterer `Step`.
  - `AlreadyCanceledTokenNeverStartsWork`.
  - `DisposeRunsOnStopOnTheThreadAndRejectsNewWork`: `onStop` läuft auf dem Thread; danach `Assert.ThrowsAsync<ObjectDisposedException>` für `InvokeAsync`; `Post` wirft nicht.
- [ ] **Step 2: Fehlschlag prüfen:** `dotnet test tests/Pagewing.Core.Tests --filter-class "*PdfiumThreadTests"` – erwartet: Build-Fehler (Typen fehlen). (Filter-Syntax von Microsoft.Testing.Platform/xunit.v3 beachten, ggf. `--filter-method`.)
- [ ] **Step 3: `PdfiumThread` implementieren.** Ein Hintergrund-`Thread` (`IsBackground = true`) mit zwei FIFO-Warteschlangen und einem Signal; Reihenfolge: erst alle `Interactive`, dann `Background`. Ein schrittweiser Auftrag führt je Durchlauf genau einen `Step()` aus und wird danach vorn in die Hintergrund-Schlange zurückgestellt (so kommen Interactive-Aufträge zwischen die Schritte). Abbruch wird vor jedem Schritt geprüft; bei Abbruch oder Ausnahme `Dispose()` des Auftrags. `onStart` läuft vor dem ersten Auftrag auf dem Thread.
- [ ] **Step 4: Tests grün:** gleicher Befehl, alle `PdfiumThreadTests` bestehen.
- [ ] **Step 5: Commit** (`feat: PdfiumThread mit Prioritäten, Schrittaufträgen und Abbruch`).

---

### Task 2: PDFium-Pakete, Drittlizenzen, Bindings, Handles

**Roadmap:** „`bblanchon.PDFium.Win32` und `.Linux` einbinden, Drittlizenzen erfassen“; „`Pagewing.Pdfium`: Bindings, SafeHandles, `PdfiumThread`“.

**Files:**
- Modify: `Directory.Packages.props`, `src/Pagewing.Pdfium/Pagewing.Pdfium.csproj` (beide `PackageReference`, `AllowUnsafeBlocks`), `Docs/DECISIONS.md`
- Create: `src/Pagewing.Pdfium/Native/Fpdf.cs`, `FpdfConstants.cs`, `Handles.cs`; `third-party/pdfium/` (Lizenzdateien + `README.md`)
- Modify: `PdfiumThread.cs` (statisches `Shared`)
- Test: `tests/Pagewing.Core.Tests/Pdfium/NativeBindingsTests.cs`, `tests/Pagewing.Core.Tests/Pagewing.Core.Tests.csproj` (Assembly-Metadatum `NuGetPackageRoot` aus `$(NuGetPackageRoot)`)

**Interfaces:**
- Consumes: `PdfiumThread` aus Task 1.
- Produces:
  - `internal static partial class Fpdf` (`[LibraryImport("pdfium")]`, Cdecl, `[SuppressGCTransition]` nur wo gemessen sinnvoll – sonst weglassen). Exakt diese Funktionen (Header: `fpdfview.h`, `fpdf_progressive.h`, `fpdf_text.h`, `fpdf_doc.h`):
    `FPDF_InitLibrary()`, `FPDF_DestroyLibrary()`, `FPDF_LoadCustomDocument(FPDF_FILEACCESS*, byte* password) → nint`, `FPDF_CloseDocument(nint)`, `FPDF_GetLastError() → CULong`, `FPDF_GetFileVersion(nint, int*) → int`, `FPDF_GetPageCount(nint) → int`, `FPDF_LoadPage(nint, int) → nint`, `FPDF_ClosePage(nint)`, `FPDF_GetPageWidthF(nint) → float`, `FPDF_GetPageHeightF(nint) → float`, `FPDFBitmap_CreateEx(int w, int h, int format, void* buffer, int stride) → nint`, `FPDFBitmap_Destroy(nint)`, `FPDF_RenderPageBitmap_Start(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags, IfsdkPause* pause) → int`, `FPDF_RenderPage_Continue(nint page, IfsdkPause* pause) → int`, `FPDF_RenderPage_Close(nint page)`, `FPDFText_LoadPage(nint) → nint`, `FPDFText_ClosePage(nint)`, `FPDFText_CountChars(nint) → int`, `FPDFText_GetText(nint, int start, int count, ushort* result) → int`, `FPDF_GetMetaText(nint doc, byte* tag, void* buffer, CULong buflen) → CULong`.
  - Structs: `FpdfFileAccess { CULong FileLen; delegate* unmanaged[Cdecl]<void*, CULong, byte*, CULong, int> GetBlock; void* Param; }`, `IfsdkPause { int Version; delegate* unmanaged[Cdecl]<IfsdkPause*, int> NeedToPauseNow; void* User; }`.
  - `internal static class FpdfConstants`: `RenderAnnot = 0x01`, `ErrSuccess..ErrPage = 0..6`, `BitmapBgrx = 3`, `RenderReady = 0`, `RenderToBeContinued = 1`, `RenderDone = 2`, `RenderFailed = 3`.
  - `abstract class PdfiumHandle : SafeHandleZeroOrMinusOneIsInvalid` (Konstruktor `(PdfiumThread thread, PdfiumHandle? parent)`), davon `DocumentHandle`, `PageHandle`, `TextPageHandle`, `BitmapHandle`. `ReleaseHandle` schließt auf dem Thread direkt, sonst per `thread.Post`; Kinder halten `parent.DangerousAddRef` bis zum eigenen Schließen. `DocumentHandle` gibt nach `FPDF_CloseDocument` den nativ allokierten `FpdfFileAccess` und das `GCHandle`/den `SafeFileHandle` des Lese-Kontexts frei.
  - `PdfiumThread.Shared` (lazy; `onStart = Fpdf.FPDF_InitLibrary`, `onStop = Fpdf.FPDF_DestroyLibrary`).

- [ ] **Step 1: Pakete eintragen:** `bblanchon.PDFium.Win32` und `bblanchon.PDFium.Linux` je `157.0.8100` in `Directory.Packages.props`; beide als `PackageReference` in `Pagewing.Pdfium.csproj`. `dotnet restore` muss grün sein.
- [ ] **Step 2: Drittlizenzen erfassen:** Aus den Release-Archiven `https://github.com/bblanchon/pdfium-binaries/releases/download/chromium%2F8100/pdfium-win-x64.tgz` und `…/pdfium-linux-x64.tgz` (in ein leeres Verzeichnis außerhalb des Repos entpacken) die Ordner `licenses/` und die Datei `LICENSE` nach `third-party/pdfium/` kopieren (Dateien beider Archive vereinigen, Unterschiede im README nennen). `third-party/pdfium/README.md` nennt Version (`VERSION`-Datei), Quelle, Build-Argumente (`pdf_enable_v8 = false`, `pdf_enable_xfa = false` aus `args.gn`) und dass das NuGet-Paket selbst keine Lizenzdateien enthält. Hinweis: Auslieferung im Paket und im Über-Dialog folgt in M8.
- [ ] **Step 3: Tests schreiben** (`NativeBindingsTests`):
  - `NativeLibraryLoadsAndInitializes`: `await PdfiumThread.Shared.InvokeAsync(() => Fpdf.FPDF_GetLastError())` liefert 0, ohne `DllNotFoundException`.
  - `ConstantsMatchThePackageHeaders`: liest `fpdfview.h` und `fpdf_progressive.h` aus `<NuGetPackageRoot>/bblanchon.pdfium.win32/157.0.8100/build/native/include/pdfium/` (Version aus der Assembly-Metadatum-Konstante, nicht hartkodiert, wenn möglich aus `Directory.Packages.props` lesen) und prüft jede Konstante aus `FpdfConstants` gegen das passende `#define` (`FPDF_ANNOT`, `FPDF_ERR_*`, `FPDFBitmap_BGRx`, `FPDF_RENDER_*`).
  - `NoV8PackagesAreReferenced`: `Directory.Packages.props` enthält kein `PDFiumV8`.
  - `HandlesNeverCloseOnForeignThreads`: ein `DocumentHandle`-Ersatz (Test-Unterklasse von `PdfiumHandle`) wird auf dem Testthread freigegeben; sein Schließen läuft auf dem `PdfiumThread` (Thread-ID protokollieren).
- [ ] **Step 4: Fehlschlag prüfen** (`dotnet test tests/Pagewing.Core.Tests --filter-class "*NativeBindingsTests"`): Build-Fehler.
- [ ] **Step 5: Bindings, Konstanten, Handles, `Shared` implementieren** wie unter Interfaces. `AllowUnsafeBlocks` nur in diesem Projekt.
- [ ] **Step 6: Tests grün**, danach `dotnet build Pagewing.slnx` ohne Warnungen.
- [ ] **Step 7: `Docs/DECISIONS.md`** (neuer Eintrag oben): PDFium 157.0.8100, Dateizugriff per `FPDF_LoadCustomDocument`, `CULong`-Regel, Handle-Regeln, ein gemeinsamer Thread, Kacheln über `_Start` mit Offset statt `WithMatrix`, BenchmarkDotNet als Entwicklungs-Abhängigkeit (MIT, nur `bench/`).
- [ ] **Step 8: Commit** (`feat: PDFium-Bindings, Handles und Drittlizenzen`).

---

### Task 3: Test-PDFs und Generator

**Roadmap:** (Teil von „`testdata/pdfs` mit lizenzklaren Test-PDFs und `LICENSES.md`“, abhaken in Task 9)

**Files:**
- Create: `tools/make-testpdfs.cs`, `testdata/pdfs/*.pdf`, `testdata/pdfs/LICENSES.md`, `testdata/pdfs/LICENSE-pdfium.txt`, `tests/Shared/TestData.cs`
- Modify: `.github/workflows/ci.yml` (Schritt „Check the test PDFs“ vor „Build“: `dotnet run tools/make-testpdfs.cs -- check`)

**Interfaces:**
- Produces: `static class TestData { string Root; string Pdf(string name); string ReferenceDir(string pdfStem); }` (Namespace `Pagewing.TestSupport`; sucht das Repo-Root über `Pagewing.slnx` ausgehend von `AppContext.BaseDirectory`). Dateien in `testdata/pdfs/`, alle mit Info-Dictionary (`Title`, `Producer = Pagewing test generator`), Seiten höchstens 300×400 pt, ohne Kompression, ohne Zeitstempel:
  - `text-basic.pdf` (2 Seiten, Helvetica und Times, Text `Hello, Pagewing` auf Seite 1 und `Größe äöü` (WinAnsi) auf Seite 2, Titel mit Umlaut als UTF-16BE-String)
  - `vector-shapes.pdf` (Rechtecke, Kurven, Strich/Füllung, Strichelung, Clipping, axiales Shading)
  - `image-rgb.pdf` (RGB-Bild 64×64 unkomprimiert, dazu Bild mit SMask)
  - `transparency.pdf` (`ca`/`CA`, Blend-Mode Multiply)
  - `rotated-page.pdf` (MediaBox 200×100, `/Rotate 90`, Text)
  - `cropbox.pdf` (MediaBox 300×300, CropBox 200×150)
  - `annotations.pdf` (Square- und Highlight-Annotation mit Appearance-Stream, Link-Annotation)
  - `wide-page.pdf` (600×120 pt, Verlauf und Text; für den Kacheltest)
  - `thousand-pages.pdf` (1000 Seiten, Seite N enthält den Text `Page N`)
  - `not-a-pdf.pdf` (Text „This is not a PDF.“), `empty.pdf` (0 Byte)
  - aus PDFium `testing/resources` (BSD-3-Clause): `encrypted_hello_world_r3.pdf`, `encrypted_hello_world_r6.pdf`, `hello_world.pdf`

- [ ] **Step 1: Generator `tools/make-testpdfs.cs` schreiben** (Einzeldatei-Werkzeug, keine Pakete): `-- generate` schreibt die selbst erzeugten Dateien nach `testdata/pdfs`, `-- check` erzeugt im Speicher und vergleicht Byte für Byte (Exit 1 mit Dateinamen bei Abweichung). Ein kleiner PDF-Writer (Objekte, xref, Trailer) reicht; Ausgabe muss deterministisch sein.
- [ ] **Step 2: `generate` ausführen** und jede Datei visuell prüfen (in Chrome/Edge öffnen): Inhalte entsprechen der Liste oben.
- [ ] **Step 3: PDFium-Dateien holen:** aus `https://pdfium.googlesource.com/pdfium/+/refs/heads/main/testing/resources/<name>?format=TEXT` (Base64) in `testdata/pdfs/` ablegen. Commit-Id von `main` zum Abrufzeitpunkt (`…/pdfium/+/refs/heads/main?format=JSON`) notieren. Den BSD-3-Clause-Text mit Copyright aus dem PDFium-Repo (`LICENSE`) nach `testdata/pdfs/LICENSE-pdfium.txt`.
- [ ] **Step 4: Passwörter der verschlüsselten Dateien ermitteln** (wird in Task 4 per Test bestätigt): Kandidaten `1234`, `5678`, leer; das funktionierende Benutzer-Passwort je Datei in `LICENSES.md` festhalten. Falls beim Probieren die Bindings aus Task 2 genutzt werden, nur als Wegwerf-Code im Scratchpad, nicht einchecken.
- [ ] **Step 5: `LICENSES.md`** mit einer Tabelle je Datei: Herkunft (selbst erzeugt von `tools/make-testpdfs.cs`, GPL-3.0-or-later wie das Repo; bzw. PDFium + Commit + Pfad, BSD-3-Clause), Zweck, ggf. Passwort.
- [ ] **Step 6: `TestData.cs`** anlegen, in `Pagewing.Core.Tests.csproj` per `<Compile Include="..\Shared\TestData.cs" Link="Shared\TestData.cs" />` einbinden. Test `TestDataTests.AllListedPdfsExist` (Name ohne Unterstrich) prüft, dass jede in `LICENSES.md` genannte Datei existiert und jede `*.pdf` dort genannt ist.
- [ ] **Step 7: CI-Schritt** hinzufügen; lokal `dotnet run tools/make-testpdfs.cs -- check` → Exit 0.
- [ ] **Step 8: Commit** (`test: Test-PDFs, Generator und Lizenzübersicht`), `git show --stat HEAD` gegen die Liste prüfen.

---

### Task 4: Dokumente öffnen

**Roadmap:** „Öffnen aus Datei, auch passwortgeschützte PDFs; verständliche Fehler“.

**Files:**
- Create: `src/Pagewing.Pdfium/PdfiumDocument.cs`, `PdfiumPage.cs`, `PdfiumError.cs`; `src/Pagewing.Core/Pdf/{PdfEngine,PdfDocument,PdfMetadata,PdfPageSize,PdfExceptions}.cs`
- Test: `tests/Pagewing.Core.Tests/Pdfium/PdfiumDocumentTests.cs`, `tests/Pagewing.Core.Tests/Pdf/OpenTests.cs`

**Interfaces:**
- Consumes: `PdfiumThread.Shared`, `Fpdf`, Handles (Task 1, 2); `TestData` (Task 3).
- Produces (Pdfium, internal):
  - `enum PdfiumError { Success, Unknown, File, Format, Password, Security, Page }` (Werte wie `FPDF_ERR_*`)
  - `sealed class PdfiumOpenException : Exception { PdfiumError Error }`
  - `sealed class PdfiumDocument : IDisposable`: `static PdfiumDocument Open(PdfiumThread thread, string path, string? password)` (nur auf dem Thread; wirft `PdfiumOpenException`, `FileNotFoundException`, `DirectoryNotFoundException`, `UnauthorizedAccessException`, `IOException`), `int PageCount`, `int? FileVersion` (14 = 1.4), `string? GetMetaText(string tag)` (UTF-16LE aus `FPDF_GetMetaText`, `null` wenn leer), `PdfiumPage LoadPage(int index)` (`ArgumentOutOfRangeException` außerhalb), `PdfiumThread Thread`. Alle Mitglieder rufen `Thread.VerifyAccess()`. `Dispose()` von einem fremden Thread reiht das Schließen ein, statt zu werfen.
  - `sealed class PdfiumPage : IDisposable`: `double Width`, `double Height` (Punkte, nach Drehung und CropBox).
- Produces (Core, public, Namespace `Pagewing.Core.Pdf`):
  - `enum PdfOpenError { FileNotFound, CannotRead, NotAPdf, PasswordRequired, PasswordIncorrect, UnsupportedEncryption, Unknown }`
  - `sealed class PdfOpenException : Exception { PdfOpenError Error }` (englische, verständliche `Message`, z. B. „The file is encrypted and needs a password.“, „The password is incorrect.“, „The file is not a PDF or is damaged.“)
  - `readonly record struct PdfPageSize(double Width, double Height)`
  - `sealed record PdfMetadata(string? Title, string? Author, string? Subject, string? Keywords, string? Creator, string? Producer, string? CreationDate, string? ModDate)` (Daten roh als PDF-Datumstext)
  - `sealed class PdfEngine { PdfEngine(); Task<PdfDocument> OpenAsync(string path, string? password = null, CancellationToken ct = default); }`
  - `sealed class PdfDocument : IDisposable, IAsyncDisposable`: `int PageCount`, `string PdfVersion` („1.7“), `Task<PdfMetadata> GetMetadataAsync(ct)`, `Task<PdfPageSize> GetPageSizeAsync(int pageIndex, ct)`; `ObjectDisposedException` nach `Dispose`. (Render- und Textmethoden folgen in Task 5 und 6.)
  - Fehlerzuordnung: `FPDF_ERR_PASSWORD` mit `password == null` → `PasswordRequired`, sonst `PasswordIncorrect`; `FORMAT` → `NotAPdf`; `SECURITY` → `UnsupportedEncryption`; `DirectoryNotFoundException`/`FileNotFoundException` → `FileNotFound`; `UnauthorizedAccessException`/`IOException`/Datei > 4 GiB unter Windows → `CannotRead`; sonst `Unknown`.

- [ ] **Step 1: Tests schreiben.** `PdfiumDocumentTests` (arbeiten direkt mit `PdfiumThread.Shared`):
  - `CallFromForeignThreadThrows`: Dokument per `InvokeAsync` öffnen; auf dem Testthread werfen `doc.PageCount`, `doc.LoadPage(0)`, `doc.GetMetaText("Title")` und `PdfiumDocument.Open(...)` jeweils `PdfiumThreadViolationException` (Abnahmekriterium „fremder Thread“).
  - `DocumentClosesAfterItsPages`: Seite laden, Dokument disposen, dann Seite disposen → kein Absturz; anschließend ist ein neues Dokument öffnbar.
  - `PageIndexOutOfRangeThrows` (−1, `PageCount`).
  `OpenTests` (über `PdfEngine`):
  - `OpenReportsPageCountVersionAndSize`: `text-basic.pdf` → 2 Seiten, `PdfVersion` passt zur Datei, Seitengröße der Generatorvorgabe.
  - `OpenThousandPagesReports1000Pages`.
  - `MetadataIsReadIncludingUnicode`: Titel mit Umlaut aus `text-basic.pdf`.
  - `EncryptedFileOpensWithCorrectPassword` für `encrypted_hello_world_r3.pdf` und `_r6.pdf` (Passwörter aus `LICENSES.md`; Abnahmekriterium „richtiges Passwort“).
  - `EncryptedFileWithoutPasswordThrowsPasswordRequired` (Fehler `PasswordRequired`, `Message` enthält „password“).
  - `EncryptedFileWithWrongPasswordThrowsPasswordIncorrect` (Fehler `PasswordIncorrect`, `Message` enthält „incorrect“).
  - `NonAsciiPasswordIsPassedAsUtf8`: Test nur, dass ein falsches Passwort mit Umlauten sauber `PasswordIncorrect` liefert (keine Exception anderer Art).
  - `InvalidFilesProduceClearErrors` (Theorie): `not-a-pdf.pdf` und `empty.pdf` → `NotAPdf`; nicht existierende Datei → `FileNotFound`; ein Verzeichnis → Fehler ≠ `Unknown`.
  - `OpenFileDoesNotBlockDeleteOrReplace`: Kopie im Temp-Verzeichnis öffnen, `File.Delete` und `File.Move`-Ersetzen werfen nicht (unter Windows durch `FileShare.Delete`).
  - `UseAfterDisposeThrowsObjectDisposed`, `DisposeAsyncClosesOnThePdfiumThread`.
  - `AlreadyCanceledTokenThrowsOperationCanceled`.
- [ ] **Step 2: Fehlschlag prüfen** (`dotnet test tests/Pagewing.Core.Tests --filter-namespace "*Pdf*"` o. ä.): Build-Fehler.
- [ ] **Step 3: `PdfiumDocument.Open` implementieren:** `File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)`, Länge prüfen, Lese-Kontext per `GCHandle`, `UnmanagedCallersOnly`-Callback liest mit `RandomAccess.Read` bis zur vollen Länge und fängt jede Ausnahme (Rückgabe 0). Passwort als UTF-8 (nullterminiert), `null` → leer. Nach Fehlschlag `FPDF_GetLastError()` auslesen und Handles bereinigen.
- [ ] **Step 4: Core-Klassen implementieren** wie unter Interfaces; `PdfDocument` hält das `PdfiumDocument` und reiht jeden Zugriff über `PdfiumThread.Shared.InvokeAsync` ein.
- [ ] **Step 5: Tests grün; bei Passwort-Tests die Passwörter aus Task 3 bestätigen** (Abweichung → `LICENSES.md` korrigieren).
- [ ] **Step 6: Commit** (`feat: PDFs öffnen, auch mit Passwort, mit klaren Fehlern`).

---

### Task 5: Seiten und Kacheln rendern

**Roadmap:** „Rendern ganzer Seiten und einzelner Kacheln (Matrix + Clipping), progressiv mit Pause und Abbruch“.

**Files:**
- Create: `src/Pagewing.Pdfium/PdfiumRenderWork.cs`, `PdfiumImage.cs`; `src/Pagewing.Core/Pdf/{RenderedImage,RenderOptions,PixelRect}.cs`
- Modify: `src/Pagewing.Core/Pdf/PdfDocument.cs`, `PdfPageSize.cs` (`ToPixels(double scale)`), `PdfExceptions.cs`
- Test: `tests/Pagewing.Core.Tests/Pdfium/PdfiumRenderWorkTests.cs`, `tests/Pagewing.Core.Tests/Pdf/RenderTests.cs`

**Interfaces:**
- Consumes: Task 1, 2, 4.
- Produces (Pdfium, internal):
  - `readonly record struct PdfiumPixelRect(int X, int Y, int Width, int Height)`
  - `sealed class PdfiumImage { int Width; int Height; int Stride; byte[] Pixels /* BGRx, Alpha 0xFF */ }`
  - `sealed class PdfiumRenderWork : SteppedWork<PdfiumImage>`: Konstruktor `(PdfiumDocument doc, int pageIndex, double scale, PdfiumPixelRect? tile, bool annotations, TimeSpan slice)` (läuft auf dem Thread: lädt die Seite, bestimmt die Seiten-Pixelgröße, prüft Kachel und Größenlimit, legt Bitmap und Pause-Struktur an), `int StepCount`, `static (int Width, int Height) PagePixelSize(double widthPt, double heightPt, double scale)` = `Math.Max(1, (int)Math.Ceiling(w * scale))`. `Step()` führt genau einmal `_Start` bzw. `_Continue` aus; die Pause-Callback (`UnmanagedCallersOnly`) liefert 1, sobald `Stopwatch` die Zeitscheibe überschreitet. `Dispose()` ruft immer `FPDF_RenderPage_Close`, dann Bitmap und Seite schließen.
- Produces (Core, public):
  - `readonly record struct PixelRect(int X, int Y, int Width, int Height)`
  - `sealed class RenderedImage { int Width; int Height; int Stride; ReadOnlyMemory<byte> Pixels /* BGRA, deckend */ }`
  - `sealed record RenderOptions { bool Annotations { get; init; } = true; PdfPriority Priority { get; init; } = PdfPriority.Interactive; TimeSpan TimeSlice { get; init; } = TimeSpan.FromMilliseconds(4); }`, `enum PdfPriority { Interactive, Background }`
  - `PdfDocument.RenderPageAsync(int pageIndex, double scale, RenderOptions? options = null, CancellationToken ct = default)` und `RenderTileAsync(int pageIndex, double scale, PixelRect tile, RenderOptions? options = null, CancellationToken ct = default)`; `PdfPageSize.ToPixels(double scale) → (int Width, int Height)`
  - `sealed class PdfRenderException : Exception` (Bitmap nicht anlegbar, Renderstatus `FAILED`, mehr als 268 435 456 Pixel = 256 Mpx)
  - Flags: nur `FPDF_ANNOT` (falls `Annotations`), nie `FPDF_LCD_TEXT`; Hintergrund weiß; Rotation 0.

- [ ] **Step 1: Tests schreiben.** `RenderTests`:
  - `RenderPageHasExpectedSizeAndWhiteBackground`: `text-basic.pdf` Seite 1, Maßstab 1: Breite/Höhe = `ToPixels(1)`; Pixel (0,0) ist `FF FF FF FF`; mindestens ein Pixel ist nicht weiß.
  - `ScaleTwoDoublesTheSize`, `RenderIsDeterministic` (zweimal rendern → `SequenceEqual`).
  - `TilesStitchToTheFullPage`: `wide-page.pdf`, Maßstab 1,5, Kacheln 128×128 inklusive Randkacheln, zusammengesetzt ≙ `RenderPageAsync`. Erwartung: exakt gleich. Weicht es ab, ist die Obergrenze Δ ≤ 2 je Kanal bei höchstens 0,1 % der Pixel; die Abweichung wird dann mit Ursache in `DECISIONS.md` vermerkt.
  - `RotatedPageSwapsSize` (`rotated-page.pdf` → 100×200 pt), `CropBoxDefinesTheSize` (`cropbox.pdf` → 200×150 pt).
  - `AnnotationsAreRenderedByDefault`: `annotations.pdf` mit und ohne `Annotations` unterscheidet sich.
  - `InvalidArgumentsThrow`: Seitenindex −1 und `PageCount` → `ArgumentOutOfRangeException`; Maßstab ≤ 0 oder `NaN`; Kachel außerhalb der Seite oder mit Breite 0.
  - `HugeScaleThrowsRenderException` (Maßstab 100000) und `TooLargeTileThrowsRenderException`.
  - `ConcurrentRendersAllSucceedAndMatch`: 16 parallele Aufträge (gemischt `Interactive`/`Background`), alle Ergebnisse gleich dem Einzelrendering.
  - `CancelBeforeStartDoesNothing` und `CancelDuringRenderThrowsOperationCanceled` (Token aus einem zweiten Task bei `TimeSlice = TimeSpan.Zero`; erlaubt ist Abbruch oder normaler Abschluss, danach ist ein weiteres Rendern erfolgreich).
  - `RenderAfterDisposeThrowsObjectDisposed`.
  `PdfiumRenderWorkTests` (direkt, mit `Shared`-Thread):
  - `InterleavedTileRendersMatchSequentialRenders`: zwei Arbeiten auf **verschiedene Kacheln derselben Seite** mit `slice = TimeSpan.Zero`, abwechselnd `Step()`-Aufrufe auf dem Thread; Ergebnisse gleich dem Einzelrendering; `StepCount > 2` belegt, dass wirklich verschränkt wurde. (Falls PDFium Seitenobjekte teilt und das scheitert: je Auftrag eigene Seite laden – steht schon so im Entwurf – und den Befund in `DECISIONS.md` festhalten.)
  - `DisposeAfterFirstStepLeavesPdfiumUsable`: ein `Step()`, dann `Dispose()`, danach rendert eine neue Arbeit dieselbe Seite korrekt.
- [ ] **Step 2: Fehlschlag prüfen:** Build-Fehler.
- [ ] **Step 3: `PdfiumRenderWork` implementieren** (Verhalten wie unter Interfaces; Status `READY`/`TOBECONTINUED` → weiter, `DONE` → fertig, `FAILED` → `PdfRenderException`). Größenlimit vor jeder Allokation prüfen.
- [ ] **Step 4: Core-API** mit `InvokeSteppedAsync(() => new PdfiumRenderWork(...), priority, ct)`; Ergebnis in `RenderedImage` umwandeln (Alpha-Byte ist 0xFF, Format als BGRA dokumentieren).
- [ ] **Step 5: Tests grün.**
- [ ] **Step 6: Commit** (`feat: Seiten und Kacheln progressiv rendern`).

---

### Task 6: Textextraktion

**Roadmap:** „Textextraktion pro Seite“.

**Files:**
- Create: `src/Pagewing.Pdfium/PdfiumTextPage.cs`
- Modify: `src/Pagewing.Pdfium/PdfiumPage.cs` (`LoadTextPage()`), `src/Pagewing.Core/Pdf/PdfDocument.cs`
- Test: `tests/Pagewing.Core.Tests/Pdf/TextTests.cs`

**Interfaces:**
- Produces: `PdfiumTextPage : IDisposable { int CharCount; string GetText() }` (Puffer `CharCount + 1` UTF-16-Einheiten, ohne Terminator zurückgeben); `Task<string> PdfDocument.GetTextAsync(int pageIndex, CancellationToken ct = default)` (Zeilenumbrüche `\r\n` → `\n`, Priorität `Background`).

- [ ] **Step 1: Tests schreiben:**
  - `TextOfBasicPages`: Seite 1 von `text-basic.pdf` enthält `Hello, Pagewing`, Seite 2 `Größe äöü`.
  - `TextOfThousandPagesLastPage`: Seite 1000 ist genau `Page 1000`.
  - `PageWithoutTextReturnsEmptyString`: `vector-shapes.pdf`.
  - `RotatedPageTextIsExtracted`, `EncryptedDocumentTextIsExtracted` (`hello_world` Text der verschlüsselten Datei).
  - `TextUsesLfLineBreaks`: kein `\r` im Ergebnis.
  - `TextPageIndexOutOfRangeThrows`.
- [ ] **Step 2: Fehlschlag prüfen.**
- [ ] **Step 3: Implementieren** (`FPDFText_LoadPage` / `CountChars` / `GetText` / `ClosePage`, Handle `TextPageHandle` mit Eltern-Handle der Seite).
- [ ] **Step 4: Tests grün.**
- [ ] **Step 5: Commit** (`feat: Textextraktion pro Seite`).

---

### Task 7: PNG-Encoder

**Roadmap:** (Voraussetzung für `pagewing render`; kein eigener Roadmap-Punkt)

**Files:**
- Create: `src/Pagewing.Core/Imaging/PngEncoder.cs`, `tests/Shared/PngDecoder.cs`
- Test: `tests/Pagewing.Core.Tests/Imaging/PngEncoderTests.cs`

**Interfaces:**
- Produces: `public static class PngEncoder { static byte[] Encode(RenderedImage image); static void Encode(RenderedImage image, Stream destination); }` – 8 Bit RGB (Farbtyp 2, Alpha wird verworfen), nicht interlaced, `System.IO.Compression.ZLibStream`. `PngDecoder.Decode(byte[]) → (int Width, int Height, byte[] Rgb)` in `tests/Shared` (nur für Tests: Farbtyp 2 und 6, 8 Bit, Filter 0–4, nicht interlaced).

- [ ] **Step 1: Tests schreiben:** `StartsWithPngSignatureAndIhdr` (Maße, Bittiefe 8, Farbtyp 2); `RoundTripKeepsEveryPixel` (Verlaufsbild 37×23 mit unterschiedlichen Kanälen, BGRA → RGB geprüft); `OneByOneImageWorks`; `WideImageWorks` (3000×2); `AlphaIsIgnored` (Alpha 0 im Eingang → gleiche RGB-Pixel); `DecoderHandlesAllFilterTypes` (kleine handgebaute PNGs je Filter).
- [ ] **Step 2: Fehlschlag prüfen.**
- [ ] **Step 3: Implementieren** (Filter nach Ermessen, z. B. „Up“; Dateigröße eines weißen A4-Bilds bei 144 dpi unter 20 KB).
- [ ] **Step 4: Tests grün; `dotnet publish src/Pagewing.Cli -c Release -r win-x64` (lokal) meldet keine AOT-/Trim-Warnungen.**
- [ ] **Step 5: Commit** (`feat: PNG-Encoder`).

---

### Task 8: CLI `info`, `render`, `text`

**Roadmap:** „CLI `pagewing info|render|text`“.

**Files:**
- Modify: `src/Pagewing.Cli/Program.cs`, `src/Pagewing.Cli/Pagewing.Cli.csproj` (nur Core-Referenz bleibt), `tests/Pagewing.Core.Tests/Pagewing.Core.Tests.csproj` (ProjectReference auf Cli), `tools/smoke-cli.cs`, `README.md` (CLI-Nutzung im Abschnitt Setup)
- Create: `src/Pagewing.Cli/{CliApp,CliOptions,PageRange}.cs`, `src/Pagewing.Cli/Commands/{InfoCommand,RenderCommand,TextCommand}.cs`
- Test: `tests/Pagewing.Core.Tests/Cli/{PageRangeTests,CliTests}.cs`

**Interfaces:**
- Consumes: Core-API aus Task 4 bis 7.
- Produces:
  - `public static class CliApp { static Task<int> RunAsync(string[] args, Stream stdout, TextWriter stderr, Func<string, string?> getEnvironmentVariable, CancellationToken ct = default); }` – `Program.Main` ruft das mit `Console.OpenStandardOutput()`, `Console.Error`, `Environment.GetEnvironmentVariable`. Text auf `stdout` wird als UTF-8 ohne BOM geschrieben.
  - `static class PageRange { static IReadOnlyList<int> Parse(string text, int pageCount); }` (1-basiert, Syntax `3`, `2-5`, `1,3,5-7`, `all`; Rückgabe 0-basierte Indizes ohne Duplikate in aufsteigender Reihenfolge; `FormatException` bei ungültiger Syntax oder `0`/`5-3`, `ArgumentOutOfRangeException` jenseits von `pageCount`).
  - Befehle (Ausgaben Englisch, Zeilen `Schlüssel: Wert`):
    - `pagewing info <file> [--password <pw>]` → `File:`, `Pages:`, `PDF version:`, vorhandene Metadaten (`Title:`, `Author:`, …), dann `Page sizes:` mit zusammengefassten Bereichen, z. B. `  1-2: 200.00 x 100.00 pt`.
    - `pagewing render <file> [--pages <range>] [--dpi <n>] [--output <dir>] [--no-annotations] [--password <pw>]` → Standard `--dpi 144`, `--pages all`, Ausgabe nach `.`; Dateiname `<stem>-p<NNN>.png` (mindestens 3 Stellen, bei mehr als 999 Seiten so breit wie die Seitenzahl); Ausgabeordner wird angelegt; je geschriebene Datei eine Zeile mit dem Pfad auf stdout.
    - `pagewing text <file> [--pages <range>] [--password <pw>]` → Text je Seite, danach jeweils ein Form-Feed `\f`.
    - `pagewing --version` (wie bisher), `pagewing --help` (Hilfe auf stdout, Exit 0).
  - Passwort: `--password`, sonst Umgebungsvariable `PAGEWING_PASSWORD`. Exit-Codes: `0` Erfolg, `1` Laufzeitfehler (Meldung `pagewing: <Message>` auf stderr, nie ein Stacktrace), `2` Aufrufefehler (Meldung plus `Usage:`-Block auf stderr). Aufruf ohne Argumente: `Usage` auf stderr, Exit 2 (das prüft `tools/smoke-cli.cs` bereits).
  - Meldungen: fehlendes Passwort → „Password required. Use --password or the PAGEWING_PASSWORD environment variable.“, falsches → „The password is incorrect.“; Seite jenseits des Dokuments → „Page 9 is out of range (the document has 2 pages).“ (Exit 1).

- [ ] **Step 1: Tests schreiben.** `PageRangeTests`: gültige Fälle (`"3"`, `"2-4"`, `"1,3,5-6"`, `"all"`, `"2,2,1"` → `[0,1]`), ungültige (`"0"`, `"5-3"`, `"abc"`, `""`, `"1-"`) → `FormatException`, `"9"` bei 2 Seiten → `ArgumentOutOfRangeException`. `CliTests` (`RunAsync` mit `MemoryStream`/`StringWriter`):
  - `InfoPrintsPagesVersionAndGroupedSizes`.
  - `RenderWritesNamedPngFilesWithExpectedSize`: `text-basic.pdf --dpi 144` → `text-basic-p001.png`, `…p002.png`, PNG-Maße = `ToPixels(2)`; Ausgabeordner existiert vorher nicht.
  - `RenderPagesOptionRendersOnlyThosePages`.
  - `TextPrintsExtractedTextWithFormFeeds`.
  - `EncryptedWithoutPasswordExitsWithOneAndHint` (stderr enthält „Password required“), `WrongPasswordExitsWithOne`, `PasswordFromOptionAndFromEnvironmentWork`.
  - `MissingFileExitsWithOne` (stderr nennt die Datei).
  - `UnknownCommandAndMissingArgumentExitWithTwoAndUsage`, `InvalidPageRangeExitsWithTwo`, `PageOutOfRangeExitsWithOne`, `NoArgumentsPrintsUsageAndExitsWithTwo`.
  - `NonAsciiPathsAndUtf8OutputWork`: PDF in ein Temp-Verzeichnis `Pagewing äöü 日本` kopieren; `info`, `render` (Ausgabeordner ebenfalls mit Umlauten) und `text` gehen; stdout-Bytes von `text` für `Größe äöü` sind gültiges UTF-8 ohne BOM.
- [ ] **Step 2: Fehlschlag prüfen.**
- [ ] **Step 3: Implementieren.** Handgeschriebener Argument-Parser (keine neue Abhängigkeit); alle Core-Aufrufe asynchron; `OperationCanceledException` → Exit 1 mit „Canceled.“. `Console.CancelKeyPress` löst das Token aus.
- [ ] **Step 4: `tools/smoke-cli.cs` erweitern:** zusätzlich `info`, `render --output <temp>` und `text` gegen `testdata/pdfs/text-basic.pdf` (relativ zum aktuellen Verzeichnis = Repo-Root, wie in der CI) mit dem **veröffentlichten AOT-Binary**: Erwartung `Pages: 2`, eine PNG-Datei mit PNG-Signatur, `Hello, Pagewing` im Text. Das belegt, dass die native PDFium-Bibliothek neben dem AOT-Binary liegt.
- [ ] **Step 5: Lokal prüfen:** `dotnet test tests/Pagewing.Core.Tests` grün; `dotnet publish src/Pagewing.Cli -c Release -r win-x64 -o out/cli` ohne Warnungen; `dotnet run tools/smoke-cli.cs -- out/cli/pagewing.exe` → „Smoke-Test bestanden“. Fehlt `pdfium.dll` im Ausgabeordner, im `.csproj` die Kopie der nativen Assets sicherstellen (und in `DECISIONS.md`/`CLAUDE.md` als Stolperfalle notieren).
- [ ] **Step 6: README** (CLI-Beispiele) ergänzen.
- [ ] **Step 7: Commit** (`feat: CLI info, render und text`).

---

### Task 9: Render-Regression

**Roadmap:** „`testdata/pdfs` … `Pagewing.Render.Tests` vergleicht Renderings mit Referenzbildern“; Abnahme „`pagewing render` erzeugt … Referenzbildern übereinstimmen“.

**Files:**
- Create: `tests/Pagewing.Render.Tests/{Pagewing.Render.Tests.csproj,ImageComparer.cs,ImageComparerTests.cs,RenderCorpus.cs,RenderRegressionTests.cs}`, `testdata/references/<pdf-stem>/*.png`
- Modify: `Pagewing.slnx`, `.github/workflows/ci.yml` (Artefakt-Upload bei Fehlschlag), `.gitignore` (nur falls `render-diff/` noch nicht reicht)

**Interfaces:**
- Consumes: `CliApp.RunAsync` (Task 8), `PngDecoder`, `TestData`.
- Produces:
  - `sealed record RenderTolerance(int MaxChannelDelta, double MaxDifferingFraction)`; `static class ImageComparer { static ComparisonResult Compare(Rgb expected, Rgb actual, RenderTolerance tolerance); static byte[] CreateDiffPng(...) }`; `ComparisonResult(bool SizeMatches, long DifferingPixels, int MaxDelta, double Fraction, bool Passed)`. Ein Pixel weicht ab, wenn irgendein Kanal um mehr als `MaxChannelDelta` abweicht; die Prüfung scheitert bei anderer Größe oder wenn der Anteil abweichender Pixel `MaxDifferingFraction` übersteigt.
  - `RenderCorpus.Cases`: Liste `(string PdfName, string? Password)` für alle Test-PDFs außer `not-a-pdf`, `empty`, `thousand-pages` (stattdessen nur dessen Seiten 1 und 1000 über `--pages`), jeweils `--dpi 144`.
  - Umgebungsvariable `PAGEWING_UPDATE_REFERENCES=1`: schreibt die gerenderten Bilder als neue Referenzen und lässt den Test **trotzdem fehlschlagen** („N Referenzen aktualisiert, Abweichungen prüfen und committen“), damit es nie unbemerkt grün wird.

- [ ] **Step 1: Comparer-Tests schreiben** (`ImageComparerTests`, ohne PDFium): identisch besteht; andere Größe scheitert; Δ = Toleranz auf allen Pixeln besteht, Δ = Toleranz + 1 auf allen scheitert; abweichende Pixel knapp unter und knapp über `MaxDifferingFraction` bestehen bzw. scheitern; `CreateDiffPng` markiert abweichende Pixel rot.
- [ ] **Step 2: Fehlschlag prüfen, dann `ImageComparer` implementieren, Tests grün.**
- [ ] **Step 3: Regressionstests schreiben** (`RenderRegressionTests`):
  - `PagesMatchTheReferenceImages` (Theorie über `RenderCorpus.Cases`): rendert über `CliApp.RunAsync(["render", pdf, "--dpi", "144", "--output", <Temp>, …])`, vergleicht jede PNG mit `testdata/references/<stem>/<datei>.png`; fehlt die Referenz, scheitert der Test mit Hinweis auf `PAGEWING_UPDATE_REFERENCES`; bei Abweichung landen `<datei>.actual.png` und `<datei>.diff.png` in `<Testausgabe>/render-diff/<stem>/` und die Meldung nennt Anteil, größtes Delta und Pfade.
  - `EveryTestPdfIsInTheCorpusOrExplicitlySkipped`: jede `*.pdf` in `testdata/pdfs` steht im Korpus oder in der Skip-Liste.
  - `ComparerDetectsAOnePixelShift`: `text-basic` Seite 1 rendern, um 1 Pixel nach rechts verschieben → `Passed == false` bei der Standardtoleranz.
  - `ComparerDetectsMissingText`: Rechteck über einer Textzeile weiß füllen → `Passed == false`.
  - `RenderingTwiceIsBitIdentical` über alle Korpus-Seiten.
- [ ] **Step 4: Startwerte setzen:** `RenderTolerance(MaxChannelDelta: 16, MaxDifferingFraction: 0.002)`. Referenzen auf Windows erzeugen: `PAGEWING_UPDATE_REFERENCES=1 dotnet test tests/Pagewing.Render.Tests` (erwarteter Fehlschlag mit Aktualisierungsmeldung), alle Bilder **einzeln ansehen** (stimmen mit dem Inhalt der Test-PDFs überein), dann ohne Variable: grün. Größe der Referenzen prüfen (zusammen unter etwa 2 MB).
- [ ] **Step 5: CI-Schritt „Upload the render diffs“** nach „Test“ mit `if: failure()` und `actions/upload-artifact`, gepinnt auf den Commit-SHA der aktuellen Hauptversion (SHA per `gh api repos/actions/upload-artifact/releases/latest` bzw. Tag-Auflösung ermitteln, Versionskommentar wie bei den anderen Actions), Pfad `tests/Pagewing.Render.Tests/**/render-diff/**`, `if-no-files-found: ignore`. Auch in der Matrix-Ausprägung (Name mit `${{ matrix.name }}`).
- [ ] **Step 6: Branch pushen, CI beobachten** (`gh run watch`): Wenn `build-linux` rote Render-Tests meldet, Artefakte laden und Abweichungen ansehen. Kalibrierung: höchste gemessene Abweichung zwischen Windows und Linux ×2 als neue Toleranz, **aber** `ComparerDetectsAOnePixelShift` und `ComparerDetectsMissingText` müssen weiter bestehen. Ist beides nicht zugleich möglich (Text rendert je OS zu unterschiedlich): Ausweichlösung aus „Entscheidungen“ (Text-PDFs mit eingebetteter Schriftart, Generator und Referenzen neu), Eintrag in `DECISIONS.md`. Ergebnis (Toleranz, gemessene Werte) in `DECISIONS.md`.
- [ ] **Step 7: Commit** (`test: Render-Regression mit Referenzbildern`), `git show --stat HEAD`. CI grün auf beiden Runnern abwarten.

---

### Task 10: Benchmarks

**Roadmap:** „`Pagewing.Benchmarks` (BenchmarkDotNet): Öffnen und Rendern“.

**Files:**
- Create: `bench/Pagewing.Benchmarks/{Pagewing.Benchmarks.csproj,Program.cs,OpenBenchmarks.cs,RenderBenchmarks.cs,TextBenchmarks.cs}`
- Modify: `Pagewing.slnx` (Ordner `/bench/`), `Directory.Packages.props` (`BenchmarkDotNet` 0.15.8)

**Interfaces:**
- Consumes: Core-API, `TestData`-Logik (eigene Root-Suche, kein Test-Projekt referenzieren).
- Produces: Benchmarks `OpenSmall` (text-basic, Öffnen + Schließen), `OpenThousandPages`, `OpenThousandPagesAndRenderFirstPage` (entspricht dem Budget „1000-seitige PDF öffnen bis erste Seite“), `RenderFirstPage144Dpi`, `RenderTile512At200Percent`, `GetTextOfFirstPage`. Aufruf: `dotnet run -c Release --project bench/Pagewing.Benchmarks -- --filter "*"`.

- [ ] **Step 1: Projekt anlegen** (Konsolen-Exe, `IsPackable=false`, JIT; kein Produktcode, daher kein `IsAotCompatible`). `dotnet build Pagewing.slnx` muss ohne Warnungen laufen (Analyzer-Hinweise beheben, nicht unterdrücken).
- [ ] **Step 2: Benchmarks schreiben** wie oben, `[MemoryDiagnoser]`; `GlobalSetup` öffnet bei Bedarf Dokumente, `GlobalCleanup` schließt sie.
- [ ] **Step 3: Ausführen** (`--filter "*" --job short` genügt zum Prüfen) und die Median-Werte mit Rechnerangabe notieren; sie fließen in den `DECISIONS.md`-Eintrag in Task 12.
- [ ] **Step 4: Commit** (`feat: Benchmarks für Öffnen, Rendern und Text`).

---

### Task 11: Vergleichsmessung (Werkzeug, Protokoll, Messung durch den Projektinhaber)

**Roadmap:** „Vergleichsmessung Acrobat, Chrome, SumatraPDF …; Budgets festlegen“ (Teil 1).

**Files:**
- Create: `tools/measure-open.cs`, `bench/compare/README.md`, `bench/compare/results/<JJJJ-MM-TT>-<rechner>.csv`

**Interfaces:**
- Produces: `dotnet run tools/measure-open.cs -- --exe <pfad> --args "<argumente mit {pdf}>" --pdf <datei> --runs 10 [--window-title-contains <text>] [--second-pdf <datei>] --out <csv>` (nur Windows). Messgrößen je Lauf: **t_erstes_Bild** (erster Frame, in dem das Fenster nicht mehr dem leeren Anfangszustand entspricht und Seiteninhalt sichtbar ist: Anteil weißer Seitenfläche und mindestens ein dunkler Textpixel) und **t_visuell_fertig** (letzte sichtbare Änderung vor 500 ms Stillstand), jeweils ab Prozessstart; Ausgabe CSV und Median.

- [ ] **Step 1: Protokoll in `bench/compare/README.md`** festlegen: Fälle (a) 1-seitige PDF (`text-basic.pdf`), App nicht gestartet („Start“); (b) direkt danach wiederholt („Warmstart“); (c) `thousand-pages.pdf`; (d) zweite Datei bei laufender Instanz (nur Acrobat und SumatraPDF; Chrome öffnet einen Tab). 10 Läufe je Fall, Bildschirm unberührt, Fenster nicht verdeckt, gleicher Monitor und gleiche Skalierung, Energiesparplan „Höchstleistung“, Browser ohne Erweiterungen und Sitzungswiederherstellung. Messauflösung etwa 10 ms (Bildschirmaufnahme per `Graphics.CopyFromScreen` des Fensterbereichs; GPU-Fenster liefern bei `PrintWindow` schwarze Bilder, deshalb Bildschirmkopie). Grenzen offen benennen (Splashscreens, Animationen).
- [ ] **Step 2: Werkzeug schreiben** und gegen eine bekannte Anwendung kurz prüfen (z. B. Edge mit der Test-PDF): Ergebnisse plausibel (Wiederholungen streuen < 15 %).
- [ ] **Step 3: Commit** (`feat: Werkzeug und Protokoll für die Vergleichsmessung`).
- [ ] **Step 4: Messung durch den Projektinhaber** (nicht automatisierbar: Acrobat, Chrome, SumatraPDF und Desktop-Sitzung sind nötig): Projektinhaber anschreiben mit den Befehlen aus dem Protokoll und warten, bis die CSV-Dateien unter `bench/compare/results/` liegen. **Offen markiert, bis die Messung vorliegt.**
- [ ] **Step 5: Budgetvorschlag.** Regel: Vorschlag je Messgröße = Median des schnellsten Browsers (Chrome), nach oben auf 50 ms gerundet, **höchstens das vorläufige Budget** (nie lockern). Acrobat und SumatraPDF dienen der Einordnung. Gegenprobe: Ist der Vorschlag kleiner als die in Task 10 gemessene Kernzeit (Öffnen + erste Seite rendern) plus ein Richtwert von 50 ms für den Fensterstart, ist er unrealistisch; dann dem Projektinhaber beide Zahlen vorlegen. Die endgültige Festlegung trifft der Projektinhaber (Rückfrage mit konkretem Vorschlag), nicht der Ausführer.
- [ ] **Step 6: Ergebnis festhalten** (Task 12).

---

### Task 12: Abschluss

**Roadmap:** alle restlichen Aufgaben und Abnahmekriterien von M1, danach Status `done`.

**Files:**
- Modify: `Docs/roadmap.json` (+ `ROADMAP.md` generiert), `Docs/DECISIONS.md`, `CLAUDE.md`, `README.md`, `CHANGELOG.md`, `Docs/superpowers/specs/2026-10-16-pagewing-design.md`

- [ ] **Step 1: Gesamtprüfung lokal:**
  - `dotnet build Pagewing.slnx` → 0 Warnungen, 0 Fehler
  - `dotnet test Pagewing.slnx` → alle grün
  - `dotnet format Pagewing.slnx --verify-no-changes` → Exit 0
  - `dotnet run tools/make-testpdfs.cs -- check` → Exit 0
  - `dotnet publish src/Pagewing.Cli -c Release -r win-x64 -o out/cli` und `dotnet run tools/smoke-cli.cs -- out/cli/pagewing.exe` → „Smoke-Test bestanden“
  - `dotnet publish src/Pagewing.App -c Release -r win-x64` weiter ohne AOT-/Trim-Warnungen
- [ ] **Step 2: `Docs/DECISIONS.md`** ergänzen bzw. prüfen: PDFium-Anbindung (aus Task 2), Render-Toleranz und Kalibrierung (Task 9), Test-PDF-Generator ohne Kompression, Benchmark-Basiswerte (Task 10), **Vergleichsmessung mit Methode und Werten und die endgültigen Budgets** (Task 11).
- [ ] **Step 3: Budgets eintragen**, nachdem der Projektinhaber sie bestätigt hat: `Docs/roadmap.json` (Block „Geschwindigkeitsziele“ bzw. die Quelle der Tabelle), `CLAUDE.md` (Abschnitt „Geschwindigkeit“: „vorläufig“ streichen), Spec 5.4 und 11 („Budgets“ als erledigt). Spec 5.3 und Roadmap-Aufgabe zum Rendern: Wortlaut „Matrix + Clipping“ durch „Kacheln über `FPDF_RenderPageBitmap_Start` mit Offset“ ersetzen und unter „Scope-Änderungen“ in `roadmap.json` festhalten.
- [ ] **Step 4: `CLAUDE.md`** unter „Build-Stolperfallen“/„Projektspezifisches“: `unsigned long`-/`CULong`-Regel, Test-PDFs über `tools/make-testpdfs.cs` (`generate`/`check`), Referenzbilder nur mit `PAGEWING_UPDATE_REFERENCES=1` und Sichtprüfung, native PDFium-Bibliothek liegt neben dem AOT-Binary (Smoke-Test prüft es), Vergleichsmessung unter `bench/compare`.
- [ ] **Step 5: `README.md`** (Setup: Render-Tests, Benchmarks) und `CHANGELOG.md` `[Unreleased]` → *Added*: „CLI `pagewing info`, `render`, `text`; PDFs öffnen (auch mit Passwort), Seiten und Kacheln rendern, Text extrahieren.“
- [ ] **Step 6: Roadmap abschließen:** alle Aufgaben `done: true`; Abnahmekriterien erst abhaken, wenn geprüft, mit Nachweis in `evidence` (Testnamen, Befehl und Ergebnis, CI-Lauf-Nummer); Status `done` mit `completedOn`; `dotnet run tools/roadmap.cs -- generate`, dann `-- check` → Exit 0.
- [ ] **Step 7: Commit** (`docs: Milestone 1 abschließen`), Branch pushen, PR nach `dev` (Titel `feat: PDFium-Anbindung und Render-Kern (Milestone 1)`), CI auf beiden Runnern grün abwarten (`gh pr checks --watch`), danach laut `CLAUDE.md` selbst per Squash-Merge nach `dev` mergen.
- [ ] **Step 8: Offene Punkte melden:** alles, was nicht geprüft werden konnte (z. B. Linux-Messwerte der Vergleichsmessung, App-Start des Linux-Binaries) mit dem Befehl zur Prüfung.

---

## Selbstprüfung gegen Roadmap und Spec

- **Aufgaben:** Pakete und Lizenzen (T2), Bindings/SafeHandles/PdfiumThread (T1, T2), Öffnen mit Passwort und Fehlern (T4), Rendern Seite/Kachel progressiv mit Pause und Abbruch (T5), Text (T6), CLI (T7, T8), Testdaten und Render-Regression (T3, T9), Benchmarks (T10), Vergleichsmessung und Budgets (T11, T12).
- **Abnahme:** `pagewing render` gegen Referenzen, beide OS (T9 + CI), Fremd-Thread-Test (`CallFromForeignThreadThrows`, T4), Passwort richtig/falsch (T4, T8), Vergleichsmessung dokumentiert und Budgets festgelegt (T11, T12).
- **Spec 5.2:** Thread-Bindung (T1, T4). **5.3:** Vorschau-/Kachelmechanik als Bausteine (T5); Scheduler und Cache bleiben M3. **5.4:** Budgets (T11, T12). **7:** Toleranz, Selbsttests, Aktualisierungsschalter, Lizenzen (T3, T9).
- **Bewusst nicht in M1:** grobe Vorschau-Pipeline, LRU-Cache, Scheduler (M3); Suche (M5); Speichern (M9); Thumbnails.
