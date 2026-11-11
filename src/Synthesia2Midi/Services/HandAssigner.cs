using Synthesia2Midi.Services.Abstractions;

namespace Synthesia2Midi.Services;

/// <summary>
/// Attribue chaque note à une main d'après la couleur de la touche quand elle s'allume
/// (2026-11-11, idée utilisateur). Les vidéos Synthesia colorent chaque main d'une couleur
/// (vert/bleu, bleu/rose…) qui change d'une vidéo à l'autre : au lieu de règles fixes, on
/// regroupe les teintes en deux familles (k-moyennes à 2 groupes sur le cercle des teintes),
/// puis la famille qui joue en moyenne le plus grave devient la main gauche.
/// Canal 0 = main droite, canal 1 = main gauche (convention du reste du pipeline).
/// Si une seule couleur ressort (ou trop peu de notes colorées), tout reste en canal 0.
/// </summary>
public static class HandAssigner
{
    internal const double MinSaturation = 0.25;   // en dessous : touche peu colorée, teinte peu fiable
    private const double MinHueGap = 25;          // écart minimal entre les deux familles (degrés)
    private const double MinClusterShare = 0.05;  // une « famille » sous 5 % des notes n'est pas une main

    /// <summary>Teintes des deux mains et hauteur de partage (pour les notes à la couleur illisible).</summary>
    public sealed record HandColors(double LeftHue, double RightHue, double SplitPitch, int LeftCount, int RightCount, double LeftPitch, double RightPitch)
    {
        /// <summary>1 = main gauche, 0 = main droite.</summary>
        public int HandOf(double hue, double saturation, double pitch) =>
            saturation >= MinSaturation && !double.IsNaN(hue)
                ? (HueDistance(hue, LeftHue) < HueDistance(hue, RightHue) ? 1 : 0)
                : (pitch < SplitPitch ? 1 : 0);
    }

    /// <summary>
    /// Deux familles de teintes parmi des échantillons (teinte, saturation, hauteur). « Hauteur » =
    /// note MIDI, ou simplement l'index de la touche (seul l'ordre grave → aigu compte).
    /// Null si une seule couleur ressort ou trop peu d'échantillons colorés.
    /// </summary>
    public static HandColors? FindHandColors(IReadOnlyList<(double Hue, double Saturation, double Pitch)> samples, out string reason)
    {
        var colored = samples.Where(s => s.Saturation >= MinSaturation && !double.IsNaN(s.Hue)).ToList();
        if (colored.Count < 8)
        {
            reason = $"trop peu de notes colorées ({colored.Count})";
            return null;
        }

        var (centerA, centerB) = TwoMeansOnHueCircle(colored.Select(s => s.Hue).ToList());
        var inA = colored.Where(s => HueDistance(s.Hue, centerA) <= HueDistance(s.Hue, centerB)).ToList();
        var inB = colored.Where(s => HueDistance(s.Hue, centerA) > HueDistance(s.Hue, centerB)).ToList();

        var gap = HueDistance(centerA, centerB);
        var minShare = Math.Min(inA.Count, inB.Count) / (double)colored.Count;
        if (gap < MinHueGap || minShare < MinClusterShare)
        {
            reason = $"une seule couleur (teintes {centerA:F0}° / {centerB:F0}°, écart {gap:F0}°, part {minShare:P0})";
            return null;
        }

        var pitchA = inA.Average(s => s.Pitch);
        var pitchB = inB.Average(s => s.Pitch);
        reason = string.Empty;
        return pitchA < pitchB
            ? new HandColors(centerA, centerB, (pitchA + pitchB) / 2, inA.Count, inB.Count, pitchA, pitchB)
            : new HandColors(centerB, centerA, (pitchA + pitchB) / 2, inB.Count, inA.Count, pitchB, pitchA);
    }

    public static List<NoteEvent> Assign(IReadOnlyList<NoteEvent> events, ILogger logger)
    {
        var hands = FindHandColors(
            events.Where(e => e.IsNoteOn).Select(e => (e.Hue, e.Saturation, (double)e.MidiNote)).ToList(),
            out var reason);
        if (hands is null)
        {
            logger.LogInformation("Mains : {Reason}, pas de séparation.", reason);
            return events.Select(e => e with { Channel = 0 }).ToList();
        }

        logger.LogInformation(
            "Mains : teinte {Right:F0}° → main droite ({RightCount} notes, hauteur moy. {RightPitch:F0}), teinte {Left:F0}° → main gauche ({LeftCount} notes, hauteur moy. {LeftPitch:F0}).",
            hands.RightHue, hands.RightCount, hands.RightPitch, hands.LeftHue, hands.LeftCount, hands.LeftPitch);

        int HandOf(NoteEvent onset) => hands.HandOf(onset.Hue, onset.Saturation, onset.MidiNote);

        // Le note-off reprend le canal de son note-on (même touche).
        var openChannel = new Dictionary<int, int>();
        var result = new List<NoteEvent>(events.Count);
        foreach (var e in events)
        {
            if (e.IsNoteOn)
            {
                var channel = HandOf(e);
                openChannel[e.MidiNote] = channel;
                result.Add(e with { Channel = channel });
            }
            else
            {
                var channel = openChannel.Remove(e.MidiNote, out var c) ? c : 0;
                result.Add(e with { Channel = channel });
            }
        }

        return result;
    }

    /// <summary>Deux centres de teinte (degrés) par k-moyennes circulaires, initialisées sur l'histogramme.</summary>
    private static (double A, double B) TwoMeansOnHueCircle(IReadOnlyList<double> hues)
    {
        const int bins = 36;
        var histogram = new int[bins];
        foreach (var h in hues)
        {
            histogram[(int)(h / 10) % bins]++;
        }

        var first = Array.IndexOf(histogram, histogram.Max());
        var second = Enumerable.Range(0, bins)
            .Where(i => HueDistance(i * 10, first * 10) >= 40)
            .OrderByDescending(i => histogram[i])
            .DefaultIfEmpty((first + bins / 2) % bins)
            .First();

        double a = first * 10 + 5, b = second * 10 + 5;
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var groupA = hues.Where(h => HueDistance(h, a) <= HueDistance(h, b)).ToList();
            var groupB = hues.Where(h => HueDistance(h, a) > HueDistance(h, b)).ToList();
            var newA = groupA.Count > 0 ? CircularMean(groupA) : a;
            var newB = groupB.Count > 0 ? CircularMean(groupB) : b;
            if (Math.Abs(newA - a) < 0.1 && Math.Abs(newB - b) < 0.1)
            {
                break;
            }

            (a, b) = (newA, newB);
        }

        return (a, b);
    }

    private static double CircularMean(IEnumerable<double> degrees)
    {
        double sin = 0, cos = 0;
        foreach (var d in degrees)
        {
            sin += Math.Sin(d * Math.PI / 180);
            cos += Math.Cos(d * Math.PI / 180);
        }

        return (Math.Atan2(sin, cos) * 180 / Math.PI + 360) % 360;
    }

    internal static double HueDistance(double a, double b)
    {
        var d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }
}
