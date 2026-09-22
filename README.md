# AniWorld Requests (Jellyfin Plugin)

Dieses Plugin ermöglicht es Benutzern deines Jellyfin-Servers, direkt im Web-Interface nach Filmen und Serien auf AniWorld (und weiteren konfigurierten Providern) zu suchen und Download-Anfragen zu stellen. 

Das Plugin kommuniziert direkt mit der REST-API des [AniWorld-Downloader-Forks](https://github.com/DasEric/AniWorld-Downloader-priv-copy) und leitet freigegebene Anfragen direkt an diesen weiter. Im Connector werden ausschließlich Quellen angezeigt und akzeptiert, die in AniWorld selbst aktiviert sind. Kann dieser Status nicht sicher gelesen werden, zeigt der Connector vorsorglich keine Quellen an.

## Features
- **Nahtlose Integration:** Klinkt sich direkt in die Seitenleiste des Jellyfin Web-Clients ein.
- **Suche:** Durchsuche AniWorld und alle aktiven Quellen (STO, Kinox, FilmPalast etc.) nach Serien und Filmen.
- **Genehmigungs-System:** Benutzer können Inhalte anfragen, welche von Admins mit einem Klick freigegeben werden. Alternativ ist ein Modus für automatische Freigaben (Direct-Download) verfügbar.
- **Echtzeit-Synchronisation:** Das Plugin überwacht den Download-Status im AniWorld-Downloader und zeigt dir den Ladebalken live in Jellyfin an.
- **Downloadpfade:** Beim Einreihen wird der in AniWorld für die jeweilige Quelle festgelegte Standardpfad übernommen. Ohne Seitenstandard gilt der allgemeine AniWorld-Downloadpfad.
- **Automatischer Bibliotheksscan:** Nach vollständig erfolgreichen Downloads aktualisiert das Plugin auch ohne geöffnete Jellyfin-Seite die Film- bzw. Serienbibliotheken. Teilweise fehlgeschlagene Downloads lösen keinen Scan aus. Die AniWorld-Zielpfade müssen in den entsprechenden Jellyfin-Bibliotheken enthalten sein.

## Installation in Jellyfin

Du kannst das Plugin ganz einfach über das integrierte Repository-System von Jellyfin installieren:

1. Öffne dein Jellyfin-Dashboard und navigiere zu **Erweitert** -> **Plugins**.
2. Wechsle auf den Tab **Repositorys** und klicke auf das **+** (Hinzufügen).
3. Trage Folgendes ein:
   - **Name:** `AniWorld Connector`
   - **Repository-URL:** `https://daseric.github.io/AniWorld-Jellyfin-Connector/manifest.json`
4. Speichere das Repository und wechsle auf den Tab **Katalog**.
5. Suche nach **AniWorld Requests**, klicke auf Installieren und wähle die neueste Version aus.
6. Starte deinen Jellyfin-Server nach der Installation einmal neu.

## Einrichtung und Verbindung

Damit das Plugin Downloads starten kann, muss es mit deinem AniWorld-Downloader verknüpft werden:

### 1. API-Key im AniWorld-Downloader erstellen
1. Öffne die Weboberfläche deines AniWorld-Downloaders.
2. Gehe in die Einstellungen (Settings) zum Bereich **API Keys**.
3. Erstelle einen neuen API-Key und gib ihm **volle Zugriffsrechte** (Admin/Full Access). Diese Berechtigung wird benötigt, um Downloads zu starten und die in AniWorld aktivierten Quellen exakt zu übernehmen. Ein Read- oder Write-Key reicht für die vollständige Connector-Funktion nicht aus.
4. Kopiere dir den erstellten API-Key.

### 2. Plugin in Jellyfin konfigurieren
1. Gehe in Jellyfin auf **Dashboard** -> **Plugins** und klicke auf **AniWorld Requests**.
2. Trage die **Basis-URL** deines AniWorld-Downloaders ein (z. B. `http://192.168.178.50:8080` oder deine entsprechende Domain).
3. Füge den soeben kopierten **AniWorld API-Key** ein.
4. Speichere die Einstellungen.
5. Lade die Jellyfin-Weboberfläche in deinem Browser neu (meistens `F5` oder `Strg + F5`). An der linken Seitenleiste taucht nun der Tab "AniWorld Requests" auf!

## Migration von MediaForge
Dieses Projekt ist ein Rewrite des ehemaligen MediaForge-Connectors. Das alte MediaForge-Python-Modul wird **nicht** mehr benötigt, da dieses Plugin nun vollständig nativ mit der API des AniWorld-Downloaders spricht.


## Lizenz & Copyright
Dieses Projekt steht unter der [MIT-Lizenz](LICENSE).

**Copyright (c) 2026 Eric / @DasEric**

Jeder darf dieses Plugin nutzen, verändern und weiterverbreiten, solange der obige Copyright-Hinweis erhalten bleibt.
