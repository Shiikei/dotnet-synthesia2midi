using System.Diagnostics;
using System.Text;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Core;
using Synthesia2Midi.Services.Abstractions;

namespace Synthesia2Midi.Services;

/// <summary>
/// Invoque MuseScore Studio (mscore4) en mode headless pour générer la partition
/// à partir du .mid produit : `mscore4 in.mid -o out.musicxml` puis `mscore4 in.mid -o out.pdf`.
///
/// Nécessite un serveur X disponible (Xvfb :99, lancé par l'ENTRYPOINT du conteneur — voir
/// Dockerfile). ATTENTION : Xvfb doit être démarré SANS `-nolisten unix` — ce flag désactive
/// le socket Unix local (/tmp/.X11-unix/X99), qui est le seul canal utilisé par un client X
/// local comme mscore4 ici (`-nolisten tcp` seul suffit à désactiver l'écoute réseau, ce qui
/// est la seule restriction réellement souhaitée pour un conteneur mono-usage). Avec les deux
/// options combinées, Xvfb démarre sans erreur mais n'écoute plus nulle part : tout client X
/// échoue avec "could not connect to display" — bug diagnostiqué et corrigé le 2026-08-27.
/// </summary>
public class MuseScoreSheetMusicService(ILogger<MuseScoreSheetMusicService> logger) : ISheetMusicService
{
    public async Task<(string MusicXmlPath, string PdfPath)> RenderAsync(
        string midiPath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);

        var musicXmlPath = Path.Combine(destinationDirectory, "score.musicxml");
        var pdfPath = Path.Combine(destinationDirectory, "score.pdf");

        // MuseScore importe chaque piste/canal MIDI comme un instrument séparé : avec nos deux
        // pistes main droite / main gauche (SeparateHandsByColor), ça donnait 2 "Piano" distincts
        // côte à côte au lieu d'UN SEUL piano à 2 portées (clé de sol + clé de fa) — diagnostiqué
        // le 2026-08-27 en inspectant le MusicXML produit (2 <score-part>, l'un avec 2 portées en
        // clé de sol, l'autre 1 portée en clé de fa : pas le rendu voulu). Fix : on fusionne
        // toutes les pistes de notes en une seule piste mono-canal AVANT l'appel à mscore4,
        // uniquement pour la génération de partition — le .mid d'origine (pistes séparées par
        // main, utile pour la lecture/le téléchargement) n'est pas modifié. MuseScore répartit
        // alors lui-même les notes entre les deux portées selon la hauteur, comme pour tout
        // import MIDI mono-piste d'un piano classique. Vérifié : 1 seul <score-part>, <staves>2</staves>,
        // clé de sol (G) + clé de fa (F).
        var mergedMidiPath = Path.Combine(destinationDirectory, "_sheet_music_source.mid");
        MergeChannelsForSheetMusic(midiPath, mergedMidiPath);

        await RunMuseScoreAsync(mergedMidiPath, musicXmlPath, cancellationToken);
        await RunMuseScoreAsync(mergedMidiPath, pdfPath, cancellationToken);

        logger.LogInformation(
            "Partition générée pour {MidiPath} : {MusicXmlPath}, {PdfPath}.",
            midiPath, musicXmlPath, pdfPath);

        return (musicXmlPath, pdfPath);
    }

    private static void MergeChannelsForSheetMusic(string sourceMidiPath, string destinationMidiPath)
    {
        var sourceFile = MidiFile.Read(sourceMidiPath);

        var mergedChunk = new TrackChunk();
        using (var manager = mergedChunk.ManageTimedEvents())
        {
            foreach (var trackChunk in sourceFile.GetTrackChunks())
            {
                using var sourceManager = trackChunk.ManageTimedEvents();
                foreach (var timedEvent in sourceManager.Objects)
                {
                    var clonedEvent = timedEvent.Event.Clone();

                    // Toutes les notes (main droite ET main gauche) sur le canal 0 : pour que
                    // MuseScore les traite comme UN SEUL instrument (voir commentaire de classe).
                    // Les méta-événements (tempo, etc.) n'ont pas de canal — Clone() suffit,
                    // aucune duplication possible puisqu'ils ne sont écrits que sur la piste de
                    // tempo par DryWetMidiWriterService (une seule occurrence au total).
                    if (clonedEvent is ChannelEvent channelEvent)
                    {
                        channelEvent.Channel = (FourBitNumber)0;
                    }

                    manager.Objects.Add(new TimedEvent(clonedEvent, timedEvent.Time));
                }
            }

            manager.SaveChanges();
        }

        var mergedFile = new MidiFile(mergedChunk) { TimeDivision = sourceFile.TimeDivision };
        mergedFile.Write(destinationMidiPath, overwriteFile: true);
    }

    private async Task RunMuseScoreAsync(string midiPath, string outputPath, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "mscore4",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(midiPath);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputPath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Impossible de démarrer mscore4.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        // MuseScore écrit ses infos/avertissements (locale, libs manquantes, etc.) sur stderr
        // même en cas de succès — on ne journalise qu'en debug ici, l'échec se juge sur le
        // code de sortie ET la présence réelle du fichier produit (mscore4 peut renvoyer 0
        // tout en échouant silencieusement à écrire le fichier dans certains cas).
        logger.LogDebug("mscore4 {Midi} -o {Output} : {Stdout}{Stderr}", midiPath, outputPath, stdout, stderr);

        if (process.ExitCode != 0 || !File.Exists(outputPath))
        {
            var command = $"mscore4 {midiPath} -o {outputPath}";
            var details = new StringBuilder()
                .AppendLine($"mscore4 a échoué (code {process.ExitCode}) pour produire '{outputPath}'.")
                .AppendLine($"Commande : {command}")
                .AppendLine($"Sortie standard :\n{stdout}")
                .AppendLine($"Sortie d'erreur :\n{stderr}")
                .ToString();
            throw new InvalidOperationException(details);
        }
    }
}
