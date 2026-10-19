# .NET-Laufzeit (Native AOT) – Drittlizenzen

Pagewing ist ein Native-AOT-Programm: Die Laufzeit (GC, Typsystem) und die benötigten Teile der Klassenbibliothek sind im Binary gelinkt (MIT). Die Lizenzdateien stammen aus dem Laufzeitpaket der veröffentlichten Version 10.0.12; das Paket `microsoft.netcore.app.runtime.nativeaot.linux-x64` und das Plattformpaket des Compilers (`runtime.linux-x64.microsoft.dotnet.ilcompiler`) haben dieselbe `THIRD-PARTY-NOTICES.TXT`. Für Windows gilt derselbe Text (ein Windows-Paket stand beim Erstellen nicht zur Verfügung).

Die Build-Pakete `Microsoft.DotNet.ILCompiler` und `Microsoft.NET.ILLink.Tasks` (MIT) werden nur beim Bauen gebraucht und stehen nicht im Programm.

| Datei hier | Quelle (NuGet-Cache, nur Lesen) | SHA-256 der Originaldatei |
|---|---|---|
| `LICENSE` | `microsoft.netcore.app.runtime.nativeaot.linux-x64/10.0.12/LICENSE.TXT` | `cfc21f5e8bd655ae997eec916138b707b1d290b83272c02a95c9f821b8c87310` |
| `THIRD-PARTY-NOTICES.txt` | `microsoft.netcore.app.runtime.nativeaot.linux-x64/10.0.12/THIRD-PARTY-NOTICES.TXT` | `66f1d4e44973185519bb4aa8a9718eb22fc7af2cc532e3ae9cfc4c127ee7fc54` |

Übernommen bis auf LF-Zeilenenden unverändert.
