# Änderungen

## 1.3.0

- Eigene Kategorien für Filme, Serien/Anime und Manga sowie optional E-Books, Hörbücher und Podcasts.
- Relevanzprüfung für vollständige Suchbegriffe, Alternativtitel, Autoren und Erscheinungsjahre. Regressionstest für „gravity falls“: MangaFire gehört nicht zur Serien-Suche.
- Quellenübergreifende Relevanzsortierung unabhängig von der Antwortreihenfolge. Das Quellenlimit begrenzt Parallelität, nicht mehr die Anzahl berücksichtigter Quellen.
- Startseite mit eigenen, korrekt beschrifteten Bereichen für alle passenden aktiven Quellen; einschließlich Filmo, Moflix, MangaFire und freigegebener Kataloge.
- Serverweit durchgesetzte, standardmäßig ausgeschaltete Freigaben für E-Books, Hörbücher und Podcasts.
- Eigener Katalog-Anfrageweg mit konkreter Datei-/Formatauswahl und erneuter Prüfung bei Genehmigung. Titel- und Asset-IDs werden gespeichert; signierte Cover-URLs und Download-URLs sind keine dauerhaften Anfrageidentitäten.
- Wiederholbare Queue-Übergaben mit stabilen Anfragekennungen für H0melab Downloader 2.1.
- MegaKino-Serien behalten ihre `#mkep`-Episodenauswahl, statt mehrere Folgen versehentlich zu derselben URL zusammenzufassen.
- Katalogabschluss bedeutet „heruntergeladen“, nicht automatisch „in Jellyfin verfügbar“. Katalog- und Manga-Anfragen lösen keine Film-/Serienscans aus.
- Bestehende Videoanfragen, Plugin-ID, Konfigurationsschlüssel, API-Key-Speicherung und Jellix-Film-/Serienprotokoll bleiben erhalten.
- Der Tag-Release-Workflow führt jetzt auch die Connector-Vertragstests aus.

### Aktualisierung

1. Plugin-Konfiguration und Plugin-Datenverzeichnis sowie die Downloader-Konfiguration und Datenbank sichern. Die Sicherung vor der Aktualisierung separat behalten.
2. Neue Übergaben für die Umstellung über den vorhandenen Wartungsmodus anhalten. Bereits laufende Downloads nicht erneut einreichen.
3. Zuerst H0melab Downloader **2.1.0** veröffentlichen und einsetzen. Dessen Docker-Workflow startet beim **Veröffentlichen einer GitHub-Release**, nicht allein durch einen Versions-Tag.
4. Danach Connector **1.3.0** einsetzen. Sein Release-/Repository-Workflow startet beim Push von **v1.3.0**. Er erzeugt selbst die GitHub-Release; keine zweite Release dafür manuell anlegen.
5. Bestehende Film-/Serien-Anfragen prüfen, Kategorien und alle gewünschten Quellen kontrollieren und den Wartungsmodus aufheben.
6. Gewünschte Katalogkategorien ausdrücklich im Jellyfin-Plugin freigeben. Einzeln Suche, Dateiauswahl, Genehmigung und Abschluss testen. Die Downloader-Ausgabeverzeichnisse müssen für die vorgesehene Bibliotheksverwaltung erreichbar sein.

Ältere Downloader ohne Quellenbeschreibung behalten den bisherigen Videozugang. Vollständige Kategorien, Kataloganfragen und die neue Startseite setzen Downloader 2.1 voraus.

### Daten und Rückweg

Der JSON-Anfragespeicher wird additiv erweitert. Alte MangaFire-Anfragen erhalten beim Einlesen die Kategorie Manga. IDs, Entscheidungen und Queue-Verknüpfungen bleiben erhalten. Neue Katalogkategorien starten deaktiviert.

Bei einer Rückkehr zu alten Programmversionen zunächst neue Übergaben anhalten und die zusammengehörige vorherige Plugin-/Datensicherung wiederherstellen. Neue Kataloganfragen sind kein unterstütztes Eingabeformat für alte Plugin-Versionen. Downloader-Queue-IDs und bereits geladene Dateien vor erneuten Übergaben abgleichen; ein Programm-Rollback macht Downloads nicht rückgängig.

### Prüfen

- Connector: `dotnet run --project Tests/Connector.ContractTests/Connector.ContractTests.csproj --configuration Release`
- Release-Metadaten: vorhandenen Release-Validator mit Tag `v1.3.0` aufrufen.
- Plugin: Release-Build mit `-warnaserror`.
- Downloader: gesamte vorhandene pytest-Suite und Ruff-Prüfungen.

Die automatisierten Vertragstests verwenden isolierte Datenverzeichnisse und nachgebildete API-Antworten. Die abschließende Prüfung mit der installierten Jellyfin-Instanz, deren Bibliotheken und den jeweils erreichbaren Live-Quellen erfolgt nach dem Deployment.
