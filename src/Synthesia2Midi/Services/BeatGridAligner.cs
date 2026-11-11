using Synthesia2Midi.Services.Abstractions;

namespace Synthesia2Midi.Services;

/// <summary>
/// Décale toutes les notes pour que le temps 0 du MIDI tombe sur un temps de la musique, celui
/// où (ou juste avant lequel) commence la première note (demande utilisateur 2026-11-11, cas
/// Sunflower : la première note arrivait au milieu de la mesure 1 dans Online Sequencer).
/// - Phase de la grille des doubles-croches : moyenne circulaire des attaques.
/// - Position des temps dans cette grille : parmi les 4 possibles, celle où commencent le plus de
///   notes longues (basses et notes tenues tombent en général sur les temps). Même critère que
///   MusicXmlScoreWriter.ComputeGridShift, qui trouve donc ensuite un décalage nul : MIDI et
///   partition restent alignés.
/// Une levée (note avant le premier temps) n'est pas forcée sur le temps : elle reste en
/// avance d'autant dans la mesure 1. Sans détection de tempo (120 BPM arbitraire), la grille
/// n'a pas de sens : on se contente d'amener la première note à 0.
/// </summary>
public static class BeatGridAligner
{
    public static IEnumerable<NoteEvent> AlignToFirstBeat(IReadOnlyList<NoteEvent> events, double bpm, bool useGrid)
    {
        if (events.Count == 0 || bpm <= 0)
        {
            return events;
        }

        var notes = PairNotes(events);
        if (notes.Count == 0)
        {
            return events;
        }

        var first = notes.Min(n => n.Start);
        var offset = useGrid && notes.Count >= 4 ? FirstBeatAtOrBefore(notes, bpm, first) : first;

        return events.Select(e => e with { TimestampSeconds = Math.Max(0, e.TimestampSeconds - offset) });
    }

    private static double FirstBeatAtOrBefore(List<(double Start, double Length)> notes, double bpm, double first)
    {
        var beat = 60.0 / bpm;
        var grid = beat / 4;

        double sin = 0, cos = 0;
        foreach (var (start, _) in notes)
        {
            var angle = 2 * Math.PI * start / grid;
            sin += Math.Sin(angle);
            cos += Math.Cos(angle);
        }

        var phase = (Math.Atan2(sin, cos) / (2 * Math.PI) + 1) % 1 * grid;

        var beatPhase = phase;
        var bestScore = double.MinValue;
        for (var k = 0; k < 4; k++)
        {
            var candidate = phase + k * grid;
            var score = notes
                .Where(n => ((long)Math.Round((n.Start - candidate) / grid) % 4 + 4) % 4 == 0)
                .Sum(n => n.Length);
            if (score > bestScore)
            {
                bestScore = score;
                beatPhase = candidate;
            }
        }

        // Dernier temps de la grille au plus tard à la première note (tolérance d'une demi-double-croche).
        var beatIndex = Math.Floor((first - beatPhase + grid / 2) / beat);
        return beatPhase + beatIndex * beat;
    }

    /// <summary>(début, durée) en secondes de chaque note, en appariant note-on / note-off par touche et canal.</summary>
    private static List<(double Start, double Length)> PairNotes(IReadOnlyList<NoteEvent> events)
    {
        var open = new Dictionary<(int, int), double>();
        var notes = new List<(double, double)>();
        foreach (var e in events.OrderBy(e => e.TimestampSeconds))
        {
            var key = (e.MidiNote, e.Channel);
            if (e.IsNoteOn)
            {
                open[key] = e.TimestampSeconds;
            }
            else if (open.Remove(key, out var start))
            {
                notes.Add((start, Math.Max(0, e.TimestampSeconds - start)));
            }
        }

        foreach (var (_, start) in open)
        {
            notes.Add((start, 0)); // note jamais relâchée avant la fin de l'extrait
        }

        return notes;
    }
}
