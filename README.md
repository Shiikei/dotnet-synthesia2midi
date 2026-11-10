# synthesia2midi — squelette C# / Blazor

Portage du service homelab Python (FastAPI/OpenCV) vers C# / Blazor Server,
selon les décisions d'architecture du brief. Ce squelette pose toute la
structure et le câblage ; l'algorithme de traitement vidéo reste à porter.

## État du squelette

**Fonctionnel dès maintenant** (une fois `dotnet restore` fait) :
- Application Blazor Server qui démarre, avec navigation (Nouvelle conversion / Historique)
- Formulaire de conversion (URL ou upload) qui écrit en base SQLite et enfile le job
- Worker `ConversionBackgroundService` qui consomme la file et fait progresser le statut
- Notifications de progression en temps réel via SignalR
- Page de détail avec téléchargements + zone d'affichage de partition (OSMD)
- Téléchargement des vidéos YouTube via yt-dlp, **implémentation complète**
  (reprend exactement la commande validée dans la version Python)
- Configuration TLS complète (`TLS_MODE=auto|pfx|pem|none`), échec explicite
  au démarrage si un mode forcé ne trouve pas ses fichiers

**Stubs à implémenter** (lèvent `NotImplementedException`, avec TODO détaillés
dans le code renvoyant aux sections du brief) :
- `FfmpegFrameDecoderService` — pipe ffmpeg rawvideo → lignes de pixels
- `KeyboardAnalysisService` — seuillage adaptatif, do central, extraction des notes
- `DryWetMidiWriterService` — écriture du .mid
- `MuseScoreSheetMusicService` — appel mscore4 headless → MusicXML/PDF

Ce sont exactement les options que tu n'as pas choisies dans cette session
(« service de décodage ffmpeg » et « Dockerfile + MuseScore headless ») — le
squelette les rend prêtes à recevoir le code dès qu'on les attaque.

## Structure

```
dotnet-synthesia2midi/
├── src/Synthesia2Midi/
│   ├── Program.cs                  # démarrage, DI, TLS, endpoints de téléchargement
│   ├── Services/
│   │   ├── TlsConfigurator.cs      # logique TLS_MODE (brief §7)
│   │   ├── ConversionQueue.cs      # Channel<Guid>
│   │   ├── ConversionBackgroundService.cs  # worker, orchestration du pipeline
│   │   ├── YtDlpVideoAcquisitionService.cs # ✅ implémenté
│   │   ├── FfmpegFrameDecoderService.cs    # ⏳ stub
│   │   ├── KeyboardAnalysisService.cs      # ⏳ stub
│   │   ├── DryWetMidiWriterService.cs      # ⏳ stub
│   │   ├── MuseScoreSheetMusicService.cs   # ⏳ stub
│   │   └── Abstractions/           # interfaces de chaque étape du pipeline
│   ├── Data/                       # AppDbContext + entité Conversion (EF Core/SQLite)
│   ├── Models/                     # ConversionSettings, ConversionStatus, DTO progression
│   ├── Hubs/ConversionProgressHub.cs
│   ├── Components/
│   │   ├── Pages/                  # Home, NewConversion, ConversionDetail, History
│   │   └── Layout/
│   └── wwwroot/                    # CSS + interop JS OpenSheetMusicDisplay
├── Dockerfile                      # ffmpeg + yt-dlp + MuseScore headless (⚠️ voir note)
├── docker-compose.yml
├── data/   (volume, vide en dev)
└── certs/  (volume, vide = TLS_MODE=none en auto)
```

## Lancer en local (sans Docker)

Prérequis : SDK .NET 9, `ffmpeg`, `yt-dlp` dans le PATH.

```bash
cd src/Synthesia2Midi
dotnet restore
DATA_DIR=./data dotnet run
```

Sans certificat dans `./data/../certs`, `TLS_MODE=auto` bascule sur HTTP :8080.

## Lancer avec Docker

```bash
docker compose up --build
```

L'AppImage MuseScore Studio est téléchargée depuis un lien direct jsDelivr
versionné (v4.7.4 au 2026-08-23) — le lien "permanent" affiché sur
musescore.org/en/download sert en réalité une page HTML de redirection JS
et ne fonctionne pas avec `wget`. **Ce lien versionné devra être mis à jour
manuellement** à chaque nouvelle release stable : va sur
https://musescore.org/en/download (section Linux), clique sur "Download
(64-bit)" dans un navigateur, et récupère le nouveau lien direct affiché sur
la page d'atterrissage ("Please use this direct link"), puis mets à jour
l'argument de build `MUSESCORE_APPIMAGE_URL` dans le Dockerfile.

## Notes techniques

- **Versions NuGet** (DryWetMIDI, EF Core Sqlite/Design) : indicatives dans le
  `.csproj`, à confirmer avec `dotnet restore` / `dotnet list package --outdated`
  puisque je n'ai pas pu interroger NuGet.org depuis ce bac à sable.
- **Schéma SQLite** : `EnsureCreated()` au démarrage pour ce squelette (pas de
  migrations EF Core générées). Passer à `Migrate()` + `dotnet ef migrations add`
  le jour où une évolution de schéma versionnée devient utile.
- **Calibration interactive** (clic/glisser sur une frame pour positionner
  `keyboard_height`) : pas encore dans l'UI — le formulaire actuel prend juste
  la valeur en numérique. À ajouter une fois l'extraction de frame (ffmpeg)
  disponible côté serveur, pour pouvoir servir une image à cliquer.

## Prochaines étapes suggérées

1. Décodage ffmpeg → C# (`FfmpegFrameDecoderService`)
2. Portage de l'algorithme d'analyse clavier (`KeyboardAnalysisService`) — les
   deux se testent ensemble sur une vraie vidéo
3. Écriture MIDI (`DryWetMidiWriterService`)
4. Dockerfile + MuseScore headless (à finaliser avant `MuseScoreSheetMusicService`)
5. Câblage complet du pipeline dans `ConversionBackgroundService`
6. Calibration interactive dans l'UI
