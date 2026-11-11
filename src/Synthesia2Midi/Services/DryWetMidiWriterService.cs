using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Synthesia2Midi.Services.Abstractions;
// DryWetMidi a lui aussi un type NoteEvent (Melanchall.DryWetMidi.Core.NoteEvent) : alias
// explicite pour lever l'ambiguïté avec le nôtre (Synthesia2Midi.Services.Abstractions.NoteEvent).
using NoteEvent = Synthesia2Midi.Services.Abstractions.NoteEvent;

namespace Synthesia2Midi.Services;

/// <summary>
/// Écriture du .mid avec DryWetMIDI — un canal MIDI par canal logique de NoteEvent
/// (0 = main droite/indifférencié, 1 = main gauche si separateHandsByColor).
///
/// ⚠️ Fichier le plus incertain de cette itération : je n'ai pas d'accès réseau/compilateur
/// pour vérifier les signatures exactes de l'API DryWetMIDI (ManageTimedEvents, TimedEvent,
/// NoteOnEvent/NoteOffEvent) contre la version 7.2.0 réellement résolue par NuGet. La logique
/// est correcte, mais attends-toi éventuellement à ajuster quelques noms/signatures au premier
/// build réel.
/// </summary>
public class DryWetMidiWriterService(ILogger<DryWetMidiWriterService> logger) : IMidiWriterService
{
    // 480 ticks/noire : cohérent avec la version Python (brief §"Algorithme de conversion").
    private const short TicksPerQuarterNote = 480;
    private const int AcousticGrandPiano = 0; // General MIDI, programme 1 (numéroté 0)

    public Task<string> WriteAsync(
        IEnumerable<NoteEvent> noteEvents,
        double bpm,
        string destinationPath,
        string? title,
        CancellationToken cancellationToken)
    {
        var midiFile = new MidiFile
        {
            TimeDivision = new TicksPerQuarterNoteTimeDivision(TicksPerQuarterNote),
        };

        // Piste de tempo seule (métadonnées) : indispensable pour que MuseScore quantifie
        // les durées de notes correctement à partir du BPM détecté (brief §"Détection du tempo").
        var tempoTrack = new TrackChunk();
        using (var tempoManager = tempoTrack.ManageTimedEvents())
        {
            // Nom du morceau (repris comme titre par Online Sequencer, Synthesia, les DAW…).
            if (!string.IsNullOrWhiteSpace(title))
            {
                tempoManager.Objects.Add(new TimedEvent(new SequenceTrackNameEvent(title.Trim()), 0));
            }

            tempoManager.Objects.Add(new TimedEvent(new SetTempoEvent(MicrosecondsPerQuarterNote(bpm)), 0));
            // Sans TimeSignature explicite dans le .mid, MuseScore doit deviner la mesure
            // depuis le regroupement des notes — bug réel observé le 2026-08-27 : un
            // morceau en 4/4 a été importé en 3/4. On n'a aucune détection de mesure dans
            // le pipeline (pas d'analyse de temps forts), donc on fixe explicitement 4/4,
            // de très loin le cas le plus fréquent pour ce type de morceaux pop/piano —
            // à revisiter si un jour une vraie détection de mesure est implémentée, ou si
            // l'utilisateur signale un morceau légitimement dans une autre mesure (3/4, 6/8...).
            tempoManager.Objects.Add(new TimedEvent(new TimeSignatureEvent(4, 4), 0));
            tempoManager.SaveChanges();
        }
        midiFile.Chunks.Add(tempoTrack);

        var eventsByChannel = noteEvents
            .GroupBy(e => Math.Clamp(e.Channel, 0, 15))
            .OrderBy(g => g.Key)
            .ToList();
        var handsSeparated = eventsByChannel.Count > 1;

        var trackCount = 0;
        foreach (var channelGroup in eventsByChannel)
        {
            var track = new TrackChunk();
            using (var manager = track.ManageTimedEvents())
            {
                // Nom de piste (canal 0 = main droite, 1 = main gauche : heuristique couleur de
                // KeyboardAnalysisService) et instrument : piano à queue (General MIDI 0). Sans
                // Program Change, chaque logiciel choisit son instrument par défaut (Online
                // Sequencer affichait « Electric Piano »).
                var trackName = !handsSeparated ? "Piano" : channelGroup.Key == 0 ? "Main droite" : "Main gauche";
                manager.Objects.Add(new TimedEvent(new SequenceTrackNameEvent(trackName), 0));
                manager.Objects.Add(new TimedEvent(
                    new ProgramChangeEvent((SevenBitNumber)AcousticGrandPiano) { Channel = (FourBitNumber)channelGroup.Key }, 0));

                foreach (var evt in channelGroup.OrderBy(e => e.TimestampSeconds))
                {
                    var ticks = SecondsToTicks(evt.TimestampSeconds, bpm);
                    var channel = (FourBitNumber)Math.Clamp(evt.Channel, 0, 15);
                    var noteNumber = (SevenBitNumber)Math.Clamp(evt.MidiNote, 0, 127);

                    MidiEvent midiEvent = evt.IsNoteOn
                        ? new NoteOnEvent(noteNumber, (SevenBitNumber)Math.Clamp(evt.Velocity, 0, 127)) { Channel = channel }
                        : new NoteOffEvent(noteNumber, SevenBitNumber.MinValue) { Channel = channel };

                    manager.Objects.Add(new TimedEvent(midiEvent, ticks));
                }

                manager.SaveChanges();
            }

            midiFile.Chunks.Add(track);
            trackCount++;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        midiFile.Write(destinationPath, overwriteFile: true);

        logger.LogInformation(
            "Fichier MIDI écrit : {Path} ({Tracks} piste(s), {Bpm} BPM).",
            destinationPath, trackCount, bpm);

        return Task.FromResult(destinationPath);
    }

    private static long SecondsToTicks(double seconds, double bpm)
        => (long)Math.Round(seconds * TicksPerQuarterNote * bpm / 60.0);

    private static long MicrosecondsPerQuarterNote(double bpm)
        => (long)Math.Round(60_000_000.0 / Math.Max(bpm, 1.0));
}
