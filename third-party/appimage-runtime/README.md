# AppImage-Runtime – Drittlizenzen

Nur das AppImage enthält diese Komponenten: den statisch gelinkten type2-Runtime des AppImage-Projekts (Release `20251108`, `tools/package.cs`, Konstante `AppImageRuntime`; Eintrag „AppImage: type2-Runtime 20251108 …“ in `Docs/DECISIONS.md`). Sie stehen in keinem Publish-Ordner, deshalb haben die Einträge in `licenses.json` keine `files`. Ihre Quelltexte: `https://github.com/AppImage/type2-runtime` (Runtime samt Bauskripten) und die unten genannten Projekte. Die Runtime nennt ihre Bestandteile selbst mit `--appimage-help` (Abschnitt „License“).

Alle Dateien sind unverändert (bis auf LF-Zeilenenden, die sie ohnehin hatten), außer `zlib-LICENSE`.

| Datei | Komponente (Version) | Quelle | SHA-256 der Originaldatei |
| --- | --- | --- | --- |
| `LICENSE` | type2-runtime `20251108` (MIT) | `https://raw.githubusercontent.com/AppImage/type2-runtime/20251108/LICENSE` | `aa154fc9070614bbe7921f89db11efd1dba7a1f3a41685958110e2230f9c0ca1` |
| `libfuse-LGPL-2.1.txt` | libfuse 3.15.0 (LGPL-2.1-only für die Bibliothek) | `https://raw.githubusercontent.com/libfuse/libfuse/fuse-3.15.0/LGPL2.txt` | `dc626520dcd53a22f727af3ee42c770e56c97a64fe3adb063799d8ab032fe551` |
| `squashfuse-LICENSE` | squashfuse 0.5.2 (BSD-2-Clause) | `https://raw.githubusercontent.com/vasi/squashfuse/0.5.2/LICENSE` | `9e909cc8a8ba27b1a649c964cf9eda37de911f3138c2b64e6d2f01703904ac13` |
| `zstd-LICENSE` | zstd 1.5.6 (BSD-3-Clause; zstd ist zusätzlich unter GPL-2.0 lizenziert, genutzt wird die BSD-Lizenz) | `https://raw.githubusercontent.com/facebook/zstd/v1.5.6/LICENSE` | `7055266497633c9025b777c78eb7235af13922117480ed5c674677adc381c9d8` |
| `zlib-LICENSE` | zlib 1.3.1 (Zlib) | Kopfkommentar von `https://raw.githubusercontent.com/madler/zlib/v1.3.1/zlib.h` (Zeilen 2 bis 24, ohne Kommentarzeichen und Einrückung; `zlib.h` hat SHA-256 `8a5579af72ea4f427ff00a4150f0ccb3fc5c1e4379f726e101133b1ab9fc600c`) | `310aa3a481cd76f612f9e1c8f6c385d080f58791f260fc287d334c0688d969df` (der Auszug) |
| `musl-COPYRIGHT` | musl libc 1.2.5 (MIT, dazu Hinweise zu weiteren Lizenzen einzelner Dateien) | `COPYRIGHT` im Tarball `https://musl.libc.org/releases/musl-1.2.5.tar.gz` (SHA-256 des Tarballs `a9a118bbe84d8764da0ea0d28b3ab3fae8477fc7e4085d90102b8596fc7c75e4`); gleich der Datei unter `https://git.musl-libc.org/cgit/musl/plain/COPYRIGHT?h=v1.2.5` | `f9bc4423732350eb0b3f7ed7e91d530298476f8fec0c6c427a1c04ade22655af` |
| `mimalloc-LICENSE` | mimalloc 2.1.7 (MIT) | `https://raw.githubusercontent.com/microsoft/mimalloc/v2.1.7/LICENSE` | `19c99805e7a44a34b297a75d1edea9985e300066dfc024d5c99d4236d4573b5d` |

Herkunft der Versionen:

- libfuse 3.15.0 und squashfuse 0.5.2 baut das Skript `scripts/common/install-dependencies.sh` des Runtime-Releases `20251108` selbst aus dem Quelltext (feste Versionen und Prüfsummen darin). Die Zeichenketten `3.15.0`, `squashfuse 0.5.2`, `1.5.6` (zstd) und `inflate 1.3.1` (zlib) stehen auch in der Runtime-Datei.
- zstd, zlib, musl und mimalloc kommen aus den Paketen von Alpine 3.21 (`scripts/docker/Dockerfile`). zstd 1.5.6 und zlib 1.3.1 sind an der Runtime-Datei geprüft. **musl 1.2.5 und mimalloc 2.1.7 sind nicht an der Datei geprüft** (beide enthalten keine Versionszeichenfolge); es sind die Versionen, die Alpine 3.21 nach unserem Wissen liefert. Beide Lizenztexte ändern sich zwischen Patch-Versionen höchstens in der Jahreszahl; bei einem neuen Runtime-Release neu abgleichen.
- zlib liefert die Lizenz nur im Kopf von `zlib.h` (und in `README`); `zlib-LICENSE` ist dieser Kommentar ohne `/*`-Rahmen und Einrückung.
