using Microsoft.AspNetCore.SignalR;
using Synthesia2Midi.Data;
using Synthesia2Midi.Data.Entities;
using Synthesia2Midi.Hubs;
using Synthesia2Midi.Models;
using Synthesia2Midi.Services.Abstractions;

namespace Synthesia2Midi.Services;

/// <summary>
/// Worker unique consommant ConversionQueue. Orchestre le pipeline complet :
/// acquisition vidéo → décodage frames → analyse clavier → écriture MIDI → partition.
///
/// La génération de partition (ISheetMusicService, toujours stub) est traitée en best-effort :
/// si elle échoue (NotImplementedException ou toute autre erreur), le MIDI reste quand même
/// disponible au téléchargement plutôt que de faire échouer toute la conversion.
/// </summary>
public class ConversionBackgroundService(
    ConversionQueue queue,
    IServiceScopeFactory scopeFactory,
    IHubContext<ConversionProgressHub> hub,
    ConversionProgressNotifier notifier,
    ILogger<ConversionBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var conversionId in queue.ReadAllAsync(stoppingToken))
        {
            using var scope = scopeFactory.CreateScope();
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<AppDbContext>();

            var conversion = await db.Conversions.FindAsync([conversionId], stoppingToken);
            if (conversion is null)
            {
                logger.LogWarning("Conversion {Id} introuvable, ignorée.", conversionId);
                continue;
            }

            try
            {
                await RunPipelineAsync(conversion, services, stoppingToken);
                conversion.Status = ConversionStatus.Completed;
                conversion.CompletedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Échec de la conversion {Id}", conversionId);
                conversion.Status = ConversionStatus.Failed;
                conversion.ErrorMessage = ex.Message;
            }
            finally
            {
                await db.SaveChangesAsync(stoppingToken);
                await ReportProgressAsync(
                    conversion.Id,
                    conversion.Status,
                    conversion.Status == ConversionStatus.Completed ? 100 : 0,
                    null,
                    stoppingToken);
            }
        }
    }

    private async Task RunPipelineAsync(Conversion conversion, IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var videoAcquisition = services.GetRequiredService<IVideoAcquisitionService>();
        var frameDecoder = services.GetRequiredService<IFrameDecoderService>();
        var keyboardAnalysis = services.GetRequiredService<IKeyboardAnalysisService>();
        var midiWriter = services.GetRequiredService<IMidiWriterService>();
        var sheetMusic = services.GetRequiredService<ISheetMusicService>();

        var settings = System.Text.Json.JsonSerializer.Deserialize<ConversionSettings>(conversion.SettingsJson)
            ?? new ConversionSettings();

        var dataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? "/data";
        var workDir = Path.Combine(dataDir, "conversions", conversion.Id.ToString());
        Directory.CreateDirectory(workDir);

        // 1. Acquisition vidéo (URL YouTube via yt-dlp, ou fichier déjà uploadé sur disque).
        await ReportProgressAsync(conversion.Id, ConversionStatus.Downloading, 10, "Récupération de la vidéo…", ct);
        string videoPath;
        if (!string.IsNullOrWhiteSpace(conversion.SourceUrl))
        {
            var acquisition = await videoAcquisition.AcquireFromUrlAsync(conversion.SourceUrl, workDir, ct);
            videoPath = acquisition.FilePath;

            // Remplace le titre provisoire (l'URL, posé par NewConversion.razor à la création)
            // par le vrai titre YouTube dès qu'on l'a — affiché dans /historique. Best-effort :
            // si yt-dlp n'a pas pu extraire de titre, on garde l'URL plutôt que de faire
            // échouer la conversion pour ça.
            if (!string.IsNullOrWhiteSpace(acquisition.Title))
            {
                conversion.Title = acquisition.Title;
                await db.SaveChangesAsync(ct);
            }
        }
        else if (!string.IsNullOrWhiteSpace(conversion.SourceFileName) && File.Exists(conversion.SourceFileName))
        {
            videoPath = conversion.SourceFileName;
        }
        else
        {
            throw new InvalidOperationException("Aucune source vidéo valide (ni URL, ni fichier uploadé introuvable).");
        }

        // 2. Décodage ffmpeg → lignes de pixels (matérialisées en mémoire : ~largeur×3 octets
        // par frame grâce au crop côté ffmpeg, donc tout à fait raisonnable même pour une
        // vidéo de plusieurs minutes).
        await ReportProgressAsync(conversion.Id, ConversionStatus.Analyzing, 30, "Décodage de la vidéo…", ct);
        var rows = new List<PixelRow>();
        await foreach (var row in frameDecoder.ReadKeyboardRowsAsync(videoPath, settings.KeyboardHeight, settings.Start, settings.End, ct))
        {
            rows.Add(row);
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException(
                "Aucune frame décodée — vérifie la vidéo et les paramètres de découpage (début/fin).");
        }

        // 3. Détection du clavier (1ère frame) + extraction des notes (toutes les frames).
        await ReportProgressAsync(conversion.Id, ConversionStatus.Analyzing, 50, "Détection du clavier et des notes…", ct);
        var layout = keyboardAnalysis.DetectLayout(rows[0].Rgb.Span);
        var noteEvents = keyboardAnalysis
            .ExtractNoteEvents(layout, rows, settings.Threshold, settings.SeparateHandsByColor)
            .ToList();

        if (noteEvents.Count == 0)
        {
            logger.LogWarning("Aucune note détectée pour la conversion {Id} — threshold trop élevé ?", conversion.Id);
        }

        // 4. Tempo : détecté si demandé, sinon 120 BPM par défaut.
        var bpm = settings.DetectTempo ? keyboardAnalysis.EstimateBpm(noteEvents) : 120;

        // 4b. Recalage : un temps de la musique tombe pile au début de la mesure 1 (sans ça, le
        // temps 0 du MIDI est l'instant « Début » de la vidéo, n'importe où dans la mesure).
        noteEvents = BeatGridAligner.AlignToFirstBeat(noteEvents, bpm, settings.DetectTempo).ToList();

        // 5. Écriture MIDI.
        await ReportProgressAsync(conversion.Id, ConversionStatus.WritingMidi, 75, "Écriture du fichier MIDI…", ct);
        var midiPath = Path.Combine(workDir, "output.mid");
        conversion.MidiFilePath = await midiWriter.WriteAsync(noteEvents, bpm, midiPath, ct);
        await db.SaveChangesAsync(ct);

        // 6. Partition (MuseScore headless) — best-effort, cf. commentaire de classe.
        await ReportProgressAsync(conversion.Id, ConversionStatus.RenderingSheetMusic, 90, "Génération de la partition…", ct);
        try
        {
            var (musicXmlPath, pdfPath) = await sheetMusic.RenderAsync(conversion.MidiFilePath, workDir, ct);
            conversion.MusicXmlFilePath = musicXmlPath;
            conversion.PdfFilePath = pdfPath;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Génération de partition indisponible pour la conversion {Id} — le MIDI reste téléchargeable.",
                conversion.Id);
        }
    }

    private async Task ReportProgressAsync(Guid conversionId, ConversionStatus status, double percent, string? message, CancellationToken ct)
    {
        var progress = new ConversionProgress(conversionId, status, percent, message);
        await notifier.PublishAsync(progress);
        await hub.Clients.Group(conversionId.ToString()).SendAsync("ProgressUpdated", progress, ct);
    }
}
