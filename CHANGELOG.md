# Änderungen

## 2.2.4

- **Kein falscher AniWorld-Fehler mehr:** AniWorld beantwortet eine Suche ohne Treffer mit einer leeren Antwort. H0melab Downloader 2.5.0 meldet das als HTTP 502 („returned no valid response“). Der Connector wertet genau diesen Fall jetzt als „keine Treffer“. Echte Ausfälle werden weiterhin angezeigt.
- **Cover aus anderen Quellen:** Liefert eine Quelle kein Bild, zum Beispiel SerienStream bei „2 Broke Girls“, übernimmt die Karte das Cover eines gleichnamigen Treffers einer anderen Quelle derselben Suche. Dafür sind keine zusätzlichen Anfragen nötig.
- **Strengere Trefferauswahl:** Titel, die nur teilweise passen, werden ausgeblendet, zum Beispiel „2-nen 2-kumi no Stadium Girl“ bei „2 broke girls“.

## 2.2.3

- **Suche quellenübergreifend:** Sobald irgendeine Quelle einen wirklich passenden Titel liefert, verschwindet der Beifang aller Quellen. Das gilt auch für Treffer, die schon angezeigt wurden, zum Beispiel MangaFire-Treffer, in denen bei „2 broke girls“ nur eine „2“ oder „Broke“ vorkommt. Passende Treffer rücken vor die übrigen. Findet keine Quelle etwas Passendes, bleiben alle Treffer sichtbar, etwa über lokalisierte Titel.

## 2.2.2

- **Suche ohne Beifang:** Liefert eine Quelle mindestens einen wirklich passenden Titel, werden unpassende Treffer dieser Quelle ausgeblendet. Ein Beispiel ist bei „2 broke girls“ jeder Film mit einer „2“ im Titel. Die besten Treffer stehen vorn.
- **Tippfehler:** Kleine Tippfehler werden toleriert („2 brok grils“, „kung fu pnada“), ebenso zusammengeschriebene Titel („brokegirls“). Zahlen und sehr kurze Wörter müssen exakt passen. Findet eine Quelle gar keinen passenden Titel, bleiben ihre Treffer unverändert, damit Treffer über lokalisierte Titel nicht verloren gehen.
- **Quellenfehler:** Eine ausgefallene Quelle erscheint als schmale Hinweiszeile über den Treffern statt als Karte im Raster.

## 2.2.1

- **Löschen im Hintergrund:** Nach dem Bestätigen schließt sich der Dialog sofort, und eine Meldung zeigt an, dass der Titel gelöscht wird. Die Karte bleibt ausgegraut mit „Wird gelöscht…“, bis der Vorgang fertig ist. Danach kommt eine Abschlussmeldung, und die Speicheranzeige aktualisiert sich. Ein Titel, der gerade gelöscht wird, lässt sich nicht erneut löschen, auch nicht aus einem anderen Tab oder Gerät. Löschaufträge laufen nacheinander, und alle Besitz- und Dateiprüfungen finden weiterhin im Auftrag statt. Der Bestätigungsdialog schreibt jetzt korrekt „1 Datei“.

## 2.2.0

- **Verschobene Downloads löschbar:** Hat der Downloader eine eigene Datei als „verändert/verschoben“ markiert (z. B. nach dem Unraid-Mover), sucht der Connector sie in der Jellyfin-Bibliothek. Maßgeblich sind gleicher Dateiname und exakt gleiche Dateigröße. Wird sie gefunden, kann der anfragende Benutzer sie in „Meine Medien“ löschen. Gelöscht werden nur die Videodatei und ihre zugehörigen Begleitdateien (gleicher Dateiname, z. B. .nfo und .srt), danach entfernt Jellyfin den Eintrag.
- **Sicherheit:** Beim Bestätigen werden Besitz, Dateiname, Größe und Änderungszeit erneut geprüft. Die Datei muss in einer Jellyfin-Bibliothek liegen und darf keine Verknüpfung sein. Dateien, die schon vor der Anfrage vorhanden waren, sowie Dateien anderer Benutzer bleiben gesperrt.
- **Cover:** Serien bekommen ihr Cover jetzt über die in Jellyfin gefundenen Episodendateien, nicht mehr nur über den Titel.
- **Speicheranzeige:** Oben neben dem Modus steht, wie viel Speicher der Film- und Serienbibliothek belegt und frei ist, mit Füllbalken. Die Farbe ist grün ab 30 % frei, orange ab 10 % und rot darunter. Bibliotheken auf derselben Platte werden nur einmal gezählt. Nach dem Löschen aktualisiert sich die Anzeige sofort.

## 2.1.1

- **Meine Medien:** Nicht löschbare Titel zeigen jetzt direkt auf der Karte den Grund. Entweder lagen die Dateien schon vor der Anfrage auf dem Server, oder sie wurden seit dem Download verändert bzw. verschoben. In beiden Fällen löscht der Downloader sie aus Sicherheitsgründen nicht.
- **Cover:** Die Suche in der Jellyfin-Bibliothek ignoriert Jahreszahlen wie „(2011)“, Satzzeichen sowie Groß- und Kleinschreibung. Dadurch werden mehr Cover gefunden. Mehrdeutige Titel zeigen weiterhin den Platzhalter.

## 2.1.0

- **Neuer Tab „Meine Medien“:** Jeder Benutzer sieht seine selbst angefragten Filme und Serien als Cover-Raster wie bei der Suche, mit Dateianzahl und Größe. Ein Klick öffnet eine Sicherheitsabfrage mit allen betroffenen Dateien. Erst nach Bestätigung werden die Dateien gelöscht und Jellyfin aktualisiert die Bibliothek, sodass Speicher frei wird.
- **Nur eigene Inhalte:** Liste, Vorschau und Löschen prüfen serverseitig den Besitz. Benutzer sehen und löschen ausschließlich Downloads, die sie selbst angefragt haben. Folgen anderer Benutzer bleiben erhalten. Das Cover stammt aus der Jellyfin-Bibliothek und dient nur der Anzeige.
- **Vollbild:** Der Anfragenbereich überdeckt jetzt die Jellyfin-Kopfleiste, statt darunter zu beginnen.
- Die Download-Liste wurde aus „Meine Anfragen“ in den neuen Tab verschoben.

## 2.0.1

- **Anfragen-Seite:** Die Anfragen-Seite aus der oberen Navigation wird wieder im H0melab-Design angezeigt. Das Overlay hatte den Seitencontainer verworfen, an dem alle Styles hängen, und zeigte deshalb unformatiertes HTML.

## 2.0.0

- **Voraussetzungen:** Jellyfin 12.4 (`targetAbi` 12.4.0.0, gebaut gegen Jellyfin 12.4.0) und H0melab Downloader 2.5.0. Zuerst den Downloader aktualisieren, dann den Connector installieren. Downloader 2.4.0 wird nicht unterstützt.
- **Anfragen im Hintergrund:** Eine Anfrage wird sofort dauerhaft gespeichert und bestätigt („XXXX wurde erfolgreich angefragt.“). Vorbereitung und Übergabe übernimmt ein Hintergrunddienst mit Wiederaufnahme nach Neustarts, begrenzten Wiederholungen und Schutz vor Doppelaufträgen. Man kann sofort weitersuchen.
- **Komplette Serien:** Eine Serienanfrage ergänzt alle verfügbaren fehlenden Folgen in der gewählten Audiosprache. Vorhandene Folgen zählen nur in dieser Sprache. Fehlende Sprachversionen bleiben sichtbar offen („teilweise erfüllt“). Technische Quellenfehler gelten nicht als fehlende Sprache.
- **Zielordner:** Filme und Serien gehen immer an die getrennten Film-/Serienziele des Downloaders. Der Inhaltstyp entscheidet, nicht die Quelle. Eine MegaKino-Serie landet damit im Serienordner. Ohne eingerichtete Ziele wird nichts übergeben, statt einen Ordner zu raten.
- **Quellen:** Alle im Downloader verfügbaren und aktivierten Quellen erscheinen, auch ZDF und DMAX. Entfernte Quellen (Kinox, BurningSeries, Cineby) sind nicht mehr enthalten. FilmPalast und Filmo können Filme und Serien liefern.
- **Suche:** Alle Treffer des Downloaders in seiner Reihenfolge, ohne zusätzliche Titelfilter oder 100er-Grenze. Fehler einer Quelle verdrängen die übrigen Ergebnisse nicht.
- **Untertitel:** Für SerienStream und ZDF optional deutsche Untertitel als Spur in der Datei; Standard sind keine Untertitel.
- **Oberfläche:** H0melab-Design für Suche, Anfragedialog, „Meine Anfragen“ und Verwaltung, auch mobil. Bestätigungen als Toast, Bestandsübersicht als Kennzahlen, Escape schließt Dialoge.
- **Wartungsmodus:** Großes Banner auf der Seite und im Anfragedialog. Anfragen, Freigaben und Übergaben sind gesperrt, solange er aktiv ist.
- **Korrekturen:** Nach dem Speichern der Plugin-Einstellungen werden die neuen Ziele sofort verwendet. Escape schließt nur noch den innersten Dialog.

## 1.5.0

- Übernimmt stabile Serientreffer aus einzelnen Episodenseiten des Downloaders, ohne künstliche Anbieter-URLs einzuführen. FilmPalast/Filmo können Filme und Serien liefern.
- Nutzt zentrale Film-/Serienziele aus Downloader 2.3.0; geprüfte Mount-/Bibliothekszuordnungen können die Ziele dort einrichten. Geänderte zentrale Ziele werden nicht von einer alten lokalen Zuordnung überschrieben.
- Bewahrt Sprachprüfung, Freigabe und Eigentumsnachweise. Größere Serien laufen als ein Auftrag mit fortsetzbaren 500er-Arbeitsabschnitten bis 20.000 Einträge statt einer stillen Kürzung.
- Explizite Capability-Prüfung und aktualisierte englische Benutzer-README. Zuerst Downloader 2.3.0, dann Connector 1.5.0 installieren. Vorhandene Medien werden nicht verschoben.

## 1.4.0

- Sprachen und Provider werden pro Folge geprüft; die Oberfläche zeigt vollständige und teilweise Sprachabdeckung. Teilanfragen benötigen eine ausdrückliche Bestätigung. Es gibt keinen stillen Wechsel der Audiosprache.
- Jellyfin-Verfügbarkeit berücksichtigt tatsächliche Audio-/Untertitelstreams einschließlich mehrerer lokaler Versionen. Unterschiedliche Sprachen gelten nicht mehr als dieselbe offene Anfrage.
- Optionale Film-/Serienzuordnung über vorhandene Downloader-Mounts und echte Jellyfin-Bibliothekspfade. Die Einrichtung prüft mit einer kurzlebigen Testdatei, dass beide Pfade dieselbe Ablage sehen. Gemischte Titel behalten pro Inhalt den richtigen Typ und das richtige Ziel.
- Neue, von der Anfragehistorie unabhängige Eigentumsdatei `download-ownership.json`; der Downloader speichert dauerhafte Nachweise neu erstellter Dateien. Übersprungene Bestandsdateien erhalten keine neue Benutzerzuordnung.
- Eigene Downloads und alle eigenen Dateien einer Serie lassen sich nach einer serverseitigen Vorschau löschen. Fremde/manuelle Dateien bleiben erhalten. Administratoren verwalten zusätzlich den gesamten Jellyfin-Film-/Serienbestand.
- Exakte, versionsgeprüfte Dateilöschung mit einmaligen benutzergebundenen Bestätigungen, Teilresultaten und anschließender Bibliotheksaktualisierung. Die normale Jellyfin-Löschberechtigung wird nicht pauschal erweitert.
- Neue/leere Jellyfin-Bibliotheken werden vor dem zielbezogenen Scan initialisiert. Gemischte Anfragen warten auf beide erforderlichen Scans.
- Englische README mit Installation, Mountzuordnung, Sprachverfügbarkeit und den Regeln für eigene Downloads.

### Aktualisierung

Zuerst den passenden **H0melab Downloader 2.2.0**, dann **Connector 1.4.0** installieren und Jellyfin neu starten. Neue Videoanfragen verwenden Connector-Protokoll 1 mit Dateinachweisen und Optionen pro Downloadteil. Jellix-Protokoll 1 bleibt unverändert. Ohne neue Zielzuordnung gelten weiterhin die bisherigen Quellenstandards; bestehende Dateien werden nicht verschoben.

Konfiguration, `requests.json`, `download-ownership.json` und die Downloader-Datenbank gemeinsam sichern. Alte Downloads werden nicht anhand von Titeln nachträglich Benutzern zugewiesen. Ein Rückweg zur vorherigen Software löscht keine Medien und stellt bereits gelöschte Dateien nicht wieder her; Dateizuordnungen für ein späteres erneutes Update aufbewahren.

Vor einem Downgrade alle neuen Connector-Downloads fertigstellen oder abbrechen. Ältere Downloader verstehen die Optionen innerhalb dieser Queue-Einträge nicht. Die zusätzlichen Dateinachweis-Tabellen bleiben für ein späteres Update erhalten.

### Verifikation

Zusätzliche C#-/Python-Regressionsprüfungen sowie ein opt-in End-to-End-Test gegen einen aus dem passenden Jellyfin-12-Quellcode gebauten Server. Dieser verwendet echte Anmeldung, Plugin-Dienste, Bibliotheken, Scans, FFprobe und Dateioperationen. Externe Quellseiten werden durch synthetische Provider ersetzt; ihre Live-Erreichbarkeit ist dadurch nicht bestätigt.

## 1.3.2

- Regressionstests für Jellix 0.5.0 bestätigen die vorhandene Protokoll-v1-Anbindung: Benutzerbindung, einmalige Auswahl, aktuelle Admin-Freigabe-/Direktdownload-Einstellung, Wartungsmodus, Quellenfreigaben und Downloadvorgaben bleiben zentral im Connector. Keine zweite Freigabepolicy in Jellix und keine Änderung am Downloader.
- Behebt einen reproduzierten Wire-Format-Fehler bei Empfehlungen: Jellyfins Standardprofil serialisiert nicht annotierte Record-Eigenschaften als `Source`, `Items` usw., die Oberfläche erwartet `source`, `items`. Die Empfehlungsabschnitte besitzen jetzt explizite JSON-Feldnamen, unabhängig vom Serializer-Profil.
- Die Regressionstests verwenden den tatsächlichen Controller mit Jellyfins Standard-, PascalCase- und CamelCase-JSON-Optionen. Ein zusätzlicher lokaler Kestrel-/MVC-HTTP-Test verwendet Jellyfins echten JSON-Output-Formatierer und prüft Empfehlungen, Suchtreffer und ausgefallene Einzelquellen.
- Allgemeine Downloader-Fehler enthalten statt der unspezifischen Meldung den tatsächlichen HTTP-Status, die Methode und den festen API-Pfad. API-Keys, Querystrings, Serveradressen und ungeprüfte Fehlerantworten werden nicht ausgegeben.
- Alle tatsächlichen Upstream-Statuscodes bleiben intern erhalten. Dadurch funktioniert insbesondere der bereits vorgesehene Fallback bei `/api/sources` mit HTTP 404 wieder.
- Die zwei Suchbereiche und die entfernte Quellenauswahl bleiben unverändert. Keine Änderung am H0melab Downloader, keine Moflix-Reparatur und keine Datenmigration.

### Aktualisierung

Nur den Connector auf **1.3.2** aktualisieren, Jellyfin neu starten und offene Webclients vollständig neu laden. Der Downloader bleibt auf **2.1.1**. Der Push des Tags **v1.3.2** startet den bestehenden Release-/Repository-Workflow; keine zweite GitHub-Release manuell erstellen.

Der Browser-/HTTP-Test prüft echte Plugin-Antworten über MVC, verwendet aber kontrollierte Downloader-Antworten. Er bestätigt keine Live-Erreichbarkeit externer Quellen. Ein aktuell ausgefallenes Moflix bleibt als Einzelquellenfehler sichtbar und darf funktionierende andere Quellen nicht unterdrücken.

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
