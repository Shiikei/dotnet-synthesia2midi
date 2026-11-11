using System.Globalization;
using System.Text;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace Synthesia2Midi.Services;

/// <summary>
/// Écrit nous-mêmes la partition MusicXML (piano, 2 portées) à partir du .mid produit, au lieu
/// de laisser MuseScore interpréter le MIDI (2026-11-11). Laissé libre, MuseScore répartissait les
/// notes à sa façon : changements de clé en cours de portée, notes très graves dans la portée de
/// clé de sol, plusieurs voix avec des silences superposés. Règles appliquées ici :
/// - portée 1 = clé de sol, portée 2 = clé de fa, jamais de changement de clé ;
/// - une seule voix par portée : les notes qui commencent ensemble forment un accord ; une note
///   tenue est coupée quand la suivante commence (lisibilité avant fidélité de la tenue) ;
/// - portée choisie par la main détectée (canal MIDI) si les mains sont séparées, sauf note
///   franchement de l'autre côté du clavier ; sinon partage au do central ;
/// - mesure 4/4, grille à la double-croche, notes recalées sur les temps (voir ComputeGridShift) ;
/// - armure estimée (la tonalité majeure qui laisse le moins de notes hors gamme).
/// MuseScore ne sert plus qu'à produire le PDF à partir de ce MusicXML.
/// </summary>
public static class MusicXmlScoreWriter
{
    private const int Divisions = 4;          // unités par noire : 1 unité = 1 double-croche
    private const int MeasureLength = 16;     // 4/4
    private const int LowestTrebleNote = 55;  // sous sol 3, une note « main droite » passe en clé de fa
    private const int HighestBassNote = 67;   // au-dessus de sol 4, une note « main gauche » passe en clé de sol
    private const int SplitPoint = 60;        // do central, quand les mains ne sont pas séparées

    private static readonly string[] Letters = ["C", "D", "E", "F", "G", "A", "B"];
    private static readonly int[] LetterPitchClasses = [0, 2, 4, 5, 7, 9, 11];

    // (durée en doubles-croches, type MusicXML, pointée, alignement requis dans la mesure)
    private static readonly (int Length, string Type, bool Dotted, int Align)[] NoteValues =
    [
        (16, "whole", false, 16), (12, "half", true, 4), (8, "half", false, 4), (6, "quarter", true, 2),
        (4, "quarter", false, 2), (3, "eighth", true, 1), (2, "eighth", false, 1), (1, "16th", false, 1),
    ];

    private sealed record ScoreNote(int Start, int End, int Pitch, int Staff);

    private sealed record Chord(int Start, int End, IReadOnlyList<int>? Pitches); // Pitches null = silence

    public static void Write(string midiPath, string musicXmlPath)
    {
        var midiFile = MidiFile.Read(midiPath);
        var ticksPerBeat = midiFile.TimeDivision is TicksPerQuarterNoteTimeDivision tpq ? tpq.TicksPerQuarterNote : 480;
        var bpm = (int)Math.Round(midiFile.GetTempoMap().GetTempoAtTime(new MidiTimeSpan(0)).BeatsPerMinute);

        var midiNotes = midiFile.GetNotes().OrderBy(n => n.Time).ToList();
        var shift = ComputeGridShift(midiNotes, ticksPerBeat);
        var grid = ticksPerBeat / 4.0;
        var channels = midiNotes.Select(n => (int)n.Channel).Distinct().OrderBy(c => c).ToList();
        var handsSeparated = channels.Count > 1;

        var notes = new List<ScoreNote>();
        foreach (var n in midiNotes)
        {
            var start = Math.Max(0, (int)Math.Round((n.Time + shift) / grid));
            var end = (int)Math.Round((n.Time + n.Length + shift) / grid);
            if (end <= start)
            {
                end = start + 1;
            }

            int pitch = n.NoteNumber;
            int staff;
            if (handsSeparated)
            {
                staff = (int)n.Channel == channels[0] ? 1 : 2;
                if (staff == 1 && pitch < LowestTrebleNote) staff = 2;
                if (staff == 2 && pitch > HighestBassNote) staff = 1;
            }
            else
            {
                staff = pitch >= SplitPoint ? 1 : 2;
            }

            notes.Add(new ScoreNote(start, end, pitch, staff));
        }

        var fifths = EstimateKey(notes);
        var keyAlter = KeyAlterations(fifths);
        var totalLength = notes.Count == 0 ? MeasureLength : notes.Max(n => n.End);
        var measureCount = Math.Max(1, (totalLength + MeasureLength - 1) / MeasureLength);

        var staves = new Dictionary<int, List<Chord>>
        {
            [1] = BuildChords(notes.Where(n => n.Staff == 1).ToList(), measureCount * MeasureLength),
            [2] = BuildChords(notes.Where(n => n.Staff == 2).ToList(), measureCount * MeasureLength),
        };

        var xml = new StringBuilder();
        xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        xml.AppendLine("<!DOCTYPE score-partwise PUBLIC \"-//Recordare//DTD MusicXML 4.0 Partwise//EN\" \"http://www.musicxml.org/dtds/partwise.dtd\">");
        xml.AppendLine("<score-partwise version=\"4.0\">");
        xml.AppendLine("<part-list><score-part id=\"P1\"><part-name>Piano</part-name></score-part></part-list>");
        xml.AppendLine("<part id=\"P1\">");

        for (var measure = 0; measure < measureCount; measure++)
        {
            var measureStart = measure * MeasureLength;
            xml.Append(CultureInfo.InvariantCulture, $"<measure number=\"{measure + 1}\">");
            if (measure == 0)
            {
                xml.Append(CultureInfo.InvariantCulture,
                    $"<attributes><divisions>{Divisions}</divisions><key><fifths>{fifths}</fifths></key>" +
                    $"<time><beats>4</beats><beat-type>4</beat-type></time><staves>2</staves>" +
                    $"<clef number=\"1\"><sign>G</sign><line>2</line></clef>" +
                    $"<clef number=\"2\"><sign>F</sign><line>4</line></clef></attributes>");
                xml.Append(CultureInfo.InvariantCulture,
                    $"<direction placement=\"above\"><direction-type><metronome><beat-unit>quarter</beat-unit>" +
                    $"<per-minute>{bpm}</per-minute></metronome></direction-type><staff>1</staff>" +
                    $"<sound tempo=\"{bpm}\"/></direction>");
            }

            foreach (var staff in new[] { 1, 2 })
            {
                if (staff == 2)
                {
                    xml.Append(CultureInfo.InvariantCulture, $"<backup><duration>{MeasureLength}</duration></backup>");
                }

                AppendStaffMeasure(xml, staves[staff], staff, measureStart, keyAlter);
            }

            xml.AppendLine("</measure>");
        }

        xml.AppendLine("</part>");
        xml.AppendLine("</score-partwise>");
        File.WriteAllText(musicXmlPath, xml.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// Décalage (en ticks, éventuellement négatif) à ajouter à toutes les notes pour que la grille
    /// des doubles-croches et les temps tombent juste. Le temps 0 du MIDI correspond à l'instant
    /// « Début » choisi dans la vidéo, pas à un temps de la musique : sans ce recalage, les notes
    /// tombaient ~0,57 temps après chaque temps (cas Kimetsu no Yaiba, 2026-11-11) et MuseScore
    /// les notait en triolets. 1) Phase sur la grille des doubles-croches : moyenne circulaire des
    /// attaques. 2) Parmi les 4 positions possibles du temps, celle où commencent le plus de notes
    /// longues (basses et notes tenues tombent en général sur les temps).
    /// </summary>
    public static double ComputeGridShift(IReadOnlyList<Note> notes, int ticksPerBeat)
    {
        if (notes.Count < 4)
        {
            return 0;
        }

        var grid = ticksPerBeat / 4.0;
        double sin = 0, cos = 0;
        foreach (var note in notes)
        {
            var angle = 2 * Math.PI * (note.Time % grid) / grid;
            sin += Math.Sin(angle);
            cos += Math.Cos(angle);
        }

        var phase = (Math.Atan2(sin, cos) / (2 * Math.PI) + 1) % 1 * grid;

        var bestOffset = phase;
        var bestScore = double.MinValue;
        for (var k = 0; k < 4; k++)
        {
            var offset = phase + k * grid;
            var score = notes
                .Where(n => ((long)Math.Round((n.Time - offset) / grid) % 4 + 4) % 4 == 0)
                .Sum(n => (double)n.Length);
            if (score > bestScore)
            {
                bestScore = score;
                bestOffset = offset;
            }
        }

        // Recule si la première note le permet (pas de temps vide au début), sinon avance.
        var firstTime = notes.Min(n => n.Time);
        return firstTime >= bestOffset - grid / 2 ? -bestOffset : ticksPerBeat - bestOffset;
    }

    /// <summary>Accords (notes qui commencent ensemble) et silences d'une portée, sans chevauchement.</summary>
    private static List<Chord> BuildChords(List<ScoreNote> staffNotes, int totalLength)
    {
        var starts = staffNotes.Select(n => n.Start).Distinct().OrderBy(s => s).ToList();
        var result = new List<Chord>();
        var cursor = 0;
        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i];
            var group = staffNotes.Where(n => n.Start == start).ToList();
            var end = group.Max(n => n.End);
            if (i + 1 < starts.Count)
            {
                end = Math.Min(end, starts[i + 1]);
            }

            if (start > cursor)
            {
                result.Add(new Chord(cursor, start, null));
            }

            result.Add(new Chord(start, end, group.Select(n => n.Pitch).Distinct().OrderBy(p => p).ToList()));
            cursor = end;
        }

        if (cursor < totalLength)
        {
            result.Add(new Chord(cursor, totalLength, null));
        }

        return result;
    }

    private static void AppendStaffMeasure(StringBuilder xml, List<Chord> chords, int staff, int measureStart, Dictionary<string, int> keyAlter)
    {
        var voice = staff == 1 ? 1 : 5;
        var measureEnd = measureStart + MeasureLength;
        var segments = new List<(int Pos, int Length, string Type, bool Dotted, Chord Chord, bool TieStart, bool TieStop)>();

        foreach (var chord in chords)
        {
            var from = Math.Max(chord.Start, measureStart);
            var to = Math.Min(chord.End, measureEnd);
            if (from >= to)
            {
                continue;
            }

            foreach (var (pos, length, type, dotted) in Decompose(from, to - from))
            {
                var isNote = chord.Pitches is not null;
                segments.Add((pos, length, type, dotted, chord, isNote && pos + length < chord.End, isNote && pos > chord.Start));
            }
        }

        if (segments.Count == 1 && segments[0].Chord.Pitches is null)
        {
            xml.Append(CultureInfo.InvariantCulture,
                $"<note><rest measure=\"yes\"/><duration>{MeasureLength}</duration><voice>{voice}</voice><staff>{staff}</staff></note>");
            return;
        }

        // Altérations en cours dans la mesure (lettre + octave) : une altération vaut jusqu'à la barre.
        var measureAlter = new Dictionary<(string, int), int>();
        foreach (var (_, length, type, dotted, chord, tieStart, tieStop) in segments)
        {
            var dot = dotted ? "<dot/>" : string.Empty;
            if (chord.Pitches is null)
            {
                xml.Append(CultureInfo.InvariantCulture,
                    $"<note><rest/><duration>{length}</duration><voice>{voice}</voice><type>{type}</type>{dot}<staff>{staff}</staff></note>");
                continue;
            }

            for (var k = 0; k < chord.Pitches.Count; k++)
            {
                var pitch = chord.Pitches[k];
                var (letter, alter) = Spell(pitch, keyAlter);
                var octave = (pitch - alter) / 12 - 1;

                var accidental = string.Empty;
                var current = measureAlter.TryGetValue((letter, octave), out var a) ? a : keyAlter[letter];
                if (!tieStop && alter != current)
                {
                    accidental = $"<accidental>{(alter > 0 ? "sharp" : alter < 0 ? "flat" : "natural")}</accidental>";
                }

                measureAlter[(letter, octave)] = alter;

                var tie = (tieStop ? "<tie type=\"stop\"/>" : string.Empty) + (tieStart ? "<tie type=\"start\"/>" : string.Empty);
                var tied = (tieStop ? "<tied type=\"stop\"/>" : string.Empty) + (tieStart ? "<tied type=\"start\"/>" : string.Empty);
                var alterXml = alter != 0 ? $"<alter>{alter}</alter>" : string.Empty;

                var chordXml = k > 0 ? "<chord/>" : string.Empty;
                var notationsXml = tied.Length > 0 ? $"<notations>{tied}</notations>" : string.Empty;
                xml.Append(CultureInfo.InvariantCulture,
                    $"<note>{chordXml}<pitch><step>{letter}</step>{alterXml}<octave>{octave}</octave></pitch>" +
                    $"<duration>{length}</duration>{tie}<voice>{voice}</voice><type>{type}</type>{dot}{accidental}<staff>{staff}</staff>" +
                    $"{notationsXml}</note>");
            }
        }
    }

    /// <summary>Découpe une durée (dans une seule mesure) en valeurs de notes écrivables, alignées sur les temps.</summary>
    private static IEnumerable<(int Pos, int Length, string Type, bool Dotted)> Decompose(int pos, int length)
    {
        while (length > 0)
        {
            var inMeasure = pos % MeasureLength;
            var value = NoteValues.First(v => v.Length <= length && inMeasure % v.Align == 0 && inMeasure + v.Length <= MeasureLength);
            yield return (pos, value.Length, value.Type, value.Dotted);
            pos += value.Length;
            length -= value.Length;
        }
    }

    /// <summary>Armure (nombre de dièses > 0 ou de bémols &lt; 0) qui laisse le moins de durée de notes hors gamme majeure.</summary>
    private static int EstimateKey(IReadOnlyList<ScoreNote> notes)
    {
        var weight = new double[12];
        foreach (var n in notes)
        {
            weight[n.Pitch % 12] += n.End - n.Start;
        }

        var best = 0;
        var bestOutside = double.MaxValue;
        for (var fifths = -6; fifths <= 6; fifths++)
        {
            var tonic = ((fifths * 7) % 12 + 12) % 12;
            var scale = new[] { 0, 2, 4, 5, 7, 9, 11 }.Select(i => (tonic + i) % 12).ToHashSet();
            var outside = Enumerable.Range(0, 12).Where(pc => !scale.Contains(pc)).Sum(pc => weight[pc]);
            if (outside < bestOutside - 1e-9 || (Math.Abs(outside - bestOutside) < 1e-9 && Math.Abs(fifths) < Math.Abs(best)))
            {
                bestOutside = outside;
                best = fifths;
            }
        }

        return best;
    }

    private static Dictionary<string, int> KeyAlterations(int fifths)
    {
        var alter = Letters.ToDictionary(l => l, _ => 0);
        if (fifths > 0)
        {
            foreach (var l in new[] { "F", "C", "G", "D", "A", "E", "B" }.Take(fifths)) alter[l] = 1;
        }
        else
        {
            foreach (var l in new[] { "B", "E", "A", "D", "G", "C", "F" }.Take(-fifths)) alter[l] = -1;
        }

        return alter;
    }

    /// <summary>Nom de note : d'abord la note de la gamme, sinon naturelle, sinon dièse (tons à dièses) ou bémol.</summary>
    private static (string Letter, int Alter) Spell(int pitch, Dictionary<string, int> keyAlter)
    {
        var pc = pitch % 12;
        var sharpKey = keyAlter.Values.All(v => v >= 0);
        var preferred = sharpKey ? 1 : -1;

        for (var i = 0; i < 7; i++)
            if (((LetterPitchClasses[i] + keyAlter[Letters[i]]) % 12 + 12) % 12 == pc) return (Letters[i], keyAlter[Letters[i]]);
        for (var i = 0; i < 7; i++)
            if (LetterPitchClasses[i] == pc) return (Letters[i], 0);
        for (var i = 0; i < 7; i++)
            if (((LetterPitchClasses[i] + preferred) % 12 + 12) % 12 == pc) return (Letters[i], preferred);
        for (var i = 0; i < 7; i++)
            if (((LetterPitchClasses[i] - preferred) % 12 + 12) % 12 == pc) return (Letters[i], -preferred);

        return ("C", 0); // inatteignable : toute classe de hauteur a une écriture ci-dessus
    }
}
