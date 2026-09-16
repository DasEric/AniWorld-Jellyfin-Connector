# AniWorld Requests (Jellyfin Plugin)

Dieses Plugin ermöglicht es Benutzern deines Jellyfin-Servers, direkt im Web-Interface nach Filmen und Serien auf AniWorld (und weiteren konfigurierten Providern) zu suchen und Download-Anfragen zu stellen.

Das Plugin kommuniziert direkt mit dem [AniWorld-Downloader](https://github.com/phoenixthrush/AniWorld-Downloader) und leitet freigegebene Anfragen direkt an diesen weiter.

## Features
- **Nahtlose Integration:** Klinkt sich in die Seitenleiste des Jellyfin Web-Clients ein.
- **Suche:** Durchsuche AniWorld und weitere konfigurierte Quellen (STO, SerienStream etc.) nach Animes, Filmen und Serien.
- **Admin-Freigaben:** Benutzer können Inhalte anfragen, welche Admins überprüfen und mit wenigen Klicks freigeben können. Optional ist auch ein Modus für automatische Freigaben (Direct-Download) verfügbar.
- **Echtzeit-Synchronisation:** Das Plugin überwacht den Download-Status im AniWorld-Downloader und zeigt den Fortschritt in Jellyfin an.

## Installation
Kompiliere das Projekt mit `dotnet build` (für das Jellyfin 12.0 SDK) und kopiere die `.dll`-Dateien in das `plugins`-Verzeichnis deines Jellyfin-Servers.

## Konfiguration
1. Gehe in Jellyfin auf **Dashboard > Plugins > AniWorld Requests**.
2. Trage die **Basis-URL** deines AniWorld-Downloaders ein (z. B. `http://localhost:8080`).
3. Gib den entsprechenden **AniWorld API-Key** an.
4. Speichere die Einstellungen und lade die Jellyfin-Weboberfläche neu.

## Migration von MediaForge
Dieses Projekt ist ein Fork/Rewrite des ehemaligen MediaForge-Connectors. Das alte MediaForge-Python-Modul wird **nicht** mehr benötigt, da dieses Plugin nun vollständig nativ mit der API des AniWorld-Downloaders spricht.
