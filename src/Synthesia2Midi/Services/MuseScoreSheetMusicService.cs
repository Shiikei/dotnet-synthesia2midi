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
        //
        // Depuis le 2026-11-11, le MusicXML est écrit par MusicXmlScoreWriter (portées, clés et voix
        // maîtrisées), puis MuseScore le met en page en PDF. L'ancien chemin (MIDI fusionné importé
        // par MuseScore) reste en secours si notre écriture échoue.
        try
        {
            MusicXmlScoreWriter.Write(midiPath, musicXmlPath);
            await RunMuseScoreAsync(musicXmlPath, pdfPath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Écriture directe de la partition impossible pour {MidiPath} — repli sur l'import MIDI de MuseScore.", midiPath);

            var mergedMidiPath = Path.Combine(destinationDirectory, "_sheet_music_source.mid");
            MergeChannelsForSheetMusic(midiPath, mergedMidiPath);

            await RunMuseScoreAsync(mergedMidiPath, musicXmlPath, cancellationToken);
            FixMusicXmlTempo(midiPath, musicXmlPath);
            await RunMuseScoreAsync(mergedMidiPath, pdfPath, cancellationToken);
        }

        logger.LogInformation(
            "Partition générée pour {MidiPath} : {MusicXmlPath}, {PdfPath}.",
            midiPath, musicXmlPath, pdfPath);

        return (musicXmlPath, pdfPath);
    }

    /// <summary>
    /// Prépare le .mid donné à MuseScore : une seule piste, canal 0, notes recalées sur la grille.
    ///
    /// Recalage (2026-11-11, cas Kimetsu no Yaiba) : le temps 0 du MIDI correspond à l'instant
    /// « Début » choisi dans la vidéo, pas à un temps de la musique. Les notes tombaient donc
    /// ~0,57 temps après chaque temps (et ~1/12 de temps à côté de la grille des doubles-croches) :
    /// MuseScore notait ces décalages en triolets et doubles-croches parasites, et ignorait le
    /// tempo du fichier (♩ = 120 au lieu de 75, donc lecture 1,6× trop rapide). On estime le
    /// décalage, on décale toutes les notes pour que les temps tombent sur les temps, puis on
    /// arrondit début et fin de chaque note à la double-croche. Le .mid téléchargeable n'est
    /// pas touché : seule la partition est concernée.
    /// </summary>
    private static void MergeChannelsForSheetMusic(string sourceMidiPath, string destinationMidiPath)
    {
        var sourceFile = MidiFile.Read(sourceMidiPath);
        var ticksPerBeat = sourceFile.TimeDivision is TicksPerQuarterNoteTimeDivision tpq
            ? tpq.TicksPerQuarterNote
            : (short)480;

        var notes = sourceFile.GetNotes().OrderBy(n => n.Time).ToList();
        var shift = MusicXmlScoreWriter.ComputeGridShift(notes, ticksPerBeat);
        var grid = ticksPerBeat / 4.0;
        long Snap(long time) => (long)(Math.Round((time + shift) / grid) * grid);

        // (temps, ordre, événement) : à temps égal, méta d'abord, puis fins de notes, puis débuts.
        var events = new List<(long Time, int Order, MidiEvent Event)>();

        // Méta-événements (tempo, mesure) : une seule occurrence, écrits par DryWetMidiWriterService.
        foreach (var timedEvent in sourceFile.GetTimedEvents())
        {
            if (timedEvent.Event is not ChannelEvent)
            {
                events.Add((timedEvent.Time == 0 ? 0 : Math.Max(0, Snap(timedEvent.Time)), 0, timedEvent.Event.Clone()));
            }
        }

        // Toutes les notes (main droite ET main gauche) sur le canal 0, pour que MuseScore
        // les traite comme UN SEUL instrument (voir commentaire de classe).
        foreach (var pitchGroup in notes.GroupBy(n => n.NoteNumber))
        {
            var snapped = pitchGroup
                .Select(n => (Start: Snap(n.Time), End: Snap(n.Time + n.Length), n.Velocity))
                .OrderBy(n => n.Start)
                .ToList();

            for (var i = 0; i < snapped.Count; i++)
            {
                var (start, end, velocity) = snapped[i];
                if (i > 0 && snapped[i - 1].Start == start)
                {
                    continue; // deux attaques fusionnées par l'arrondi : on n'en garde qu'une
                }

                if (i + 1 < snapped.Count && snapped[i + 1].Start > start)
                {
                    end = Math.Min(end, snapped[i + 1].Start); // pas de chevauchement sur la même touche
                }

                if (end <= start)
                {
                    end = start + (long)grid; // au moins une double-croche
                }

                events.Add((start, 2, new NoteOnEvent(pitchGroup.Key, velocity) { Channel = (FourBitNumber)0 }));
                events.Add((end, 1, new NoteOffEvent(pitchGroup.Key, SevenBitNumber.MinValue) { Channel = (FourBitNumber)0 }));
            }
        }

        var ordered = events.OrderBy(e => e.Time).ThenBy(e => e.Order).ToList();
        var previousTime = 0L;
        foreach (var (time, _, midiEvent) in ordered)
        {
            midiEvent.DeltaTime = time - previousTime;
            previousTime = time;
        }

        var mergedFile = new MidiFile(new TrackChunk(ordered.Select(e => e.Event)))
        {
            TimeDivision = sourceFile.TimeDivision,
        };
        mergedFile.Write(destinationMidiPath, overwriteFile: true);
    }

    /// <summary>
    /// Filet de sécurité : MuseScore a déjà écrit ♩ = 120 dans le MusicXML alors que le MIDI
    /// indiquait 75 (2026-11-11). On réécrit le tempo de la partition avec celui du MIDI, sinon
    /// la lecture dans la page est trop rapide ou trop lente.
    /// </summary>
    private void FixMusicXmlTempo(string midiPath, string musicXmlPath)
    {
        var bpm = MidiFile.Read(midiPath).GetTempoMap()
            .GetTempoAtTime(new MidiTimeSpan(0)).BeatsPerMinute;
        var bpmText = Math.Round(bpm).ToString(System.Globalization.CultureInfo.InvariantCulture);

        var xml = File.ReadAllText(musicXmlPath);
        var fixedXml = System.Text.RegularExpressions.Regex.Replace(
            xml, @"<per-minute>[^<]*</per-minute>", $"<per-minute>{bpmText}</per-minute>");
        fixedXml = System.Text.RegularExpressions.Regex.Replace(
            fixedXml, @"(<sound\b[^>]*\btempo="")[^""]*("")", $"${{1}}{bpmText}$2");

        if (fixedXml != xml)
        {
            File.WriteAllText(musicXmlPath, fixedXml);
            logger.LogInformation("Tempo de la partition corrigé à {Bpm} BPM ({Path}).", bpmText, musicXmlPath);
        }
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
