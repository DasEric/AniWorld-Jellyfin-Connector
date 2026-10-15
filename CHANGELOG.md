# Änderungen

## 1.3.1

- Genau zwei Suchbereiche nur im Jellyfin-Anfragensystem: **Serien & Filme** einschließlich Anime und Manga sowie **Bücher & Audio** für freigegebene E-Books, Hörbücher und Podcasts.
- Die Quellenauswahl entfällt. Alle passenden, aktivierten und vom Administrator erlaubten Quellen werden automatisch berücksichtigt; die Quellenherkunft bleibt an den Karten erkennbar.
- Suche und Empfehlungen fragen den Downloader ohne Kategorieparameter ab. Die Relevanzprüfung läuft ausschließlich im Connector. Fehlende Typmetadaten löschen keine Ergebnisse aus dem gemeinsamen Video-/Manga-Bereich; konkrete Typen bleiben bei Anfragen erhalten.
- Ungültige Trefferlisten werden als Quellenfehler gemeldet und nicht als „Keine Treffer“ ausgegeben. Langsame/ausgefallene Quellen blockieren andere Ergebnisse nicht.
- Manga-Anfragepläne bleiben beim Absenden Manga, statt fälschlich am Serienvergleich zu scheitern. Film-/Serientypen werden nach der Detailprüfung übernommen.
- E-Book-, Hörbuch- und Podcast-Freigaben bleiben serverseitig unabhängig wirksam. Ohne Katalogfreigabe erscheint nur der erste Suchbereich.
- Plugin-Webressourcen erhalten `Cache-Control: no-store`, um alte HTML-/JavaScript-Kombinationen nach Updates zu vermeiden.

### Aktualisierung und Rückweg

1. Vorhandene Plugin-/Downloader-Konfiguration und persistente Daten sichern; neue Übergaben bei Bedarf über den bestehenden Wartungsmodus anhalten.
2. Zuerst **H0melab Downloader 2.1.1** bereitstellen. Dort werden Kategorieauswahl und Kategorie-/Relevanzfilter der eigenständigen Oberfläche zurückgenommen. Die benötigten API-Metadaten, Queue-Idempotenz und Katalogdateien bleiben erhalten.
3. Danach **Connector 1.3.1** installieren und Jellyfin neu starten. Bereits geöffnete Jellyfin-Seiten neu laden.
4. Startseite, „gravity falls“, eine Film- und eine Manga-Anfrage sowie erlaubte Buch-/Audioformate prüfen. Bei ausgeschalteter E-Book-Freigabe darf keine E-Book-Anfrage möglich sein.
5. Keine neue Speichermigration: Anfrage-IDs, Status, konkrete Medientypen und Queue-Verknüpfungen bleiben unverändert.

Bei einem Rückweg neue Übergaben anhalten und die vorherigen Programmstände mit der zugehörigen Sicherung verwenden. Bereits laufende oder abgeschlossene Downloads nicht erneut einreichen. Der funktionale Rückbau auf diesem Patch setzt keine Datenbank zurück.

Veröffentlichung: Branch `models` und veröffentlichte GitHub-Release `v2.1.1` im Downloader; Branch `main` und Tag `v1.3.1` im Connector. Der Connector-Workflow erstellt die Release und das Repository-Manifest selbst. Bestehende Tags nicht verschieben.

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
