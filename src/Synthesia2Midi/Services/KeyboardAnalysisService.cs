using Synthesia2Midi.Services.Abstractions;

namespace Synthesia2Midi.Services;

/// <summary>
/// Portage de l'algorithme de la version Python (brief §"Algorithme de conversion")
/// + nouveautés C# (séparation des mains par couleur, estimation de tempo).
/// </summary>
public class KeyboardAnalysisService(ILogger<KeyboardAnalysisService> logger) : IKeyboardAnalysisService
{
    private const int MinKeyWidthPx = 3;
    private const double WhiteThresholdRatio = 0.60;
    private const double BlackThresholdRatio = 0.40;

    public KeyboardLayout DetectLayout(ReadOnlySpan<byte> firstFrameRow)
    {
        var pixelCount = firstFrameRow.Length / 3;
        if (pixelCount < 20)
        {
            throw new InvalidOperationException(
                "Ligne de pixels trop courte pour détecter un clavier (vérifie la résolution de la vidéo).");
        }

        var luminosities = new double[pixelCount];
        var min = double.MaxValue;
        var max = double.MinValue;
        for (var x = 0; x < pixelCount; x++)
        {
            var luminosity = Luminosity(firstFrameRow, x);
            luminosities[x] = luminosity;
            if (luminosity < min) min = luminosity;
            if (luminosity > max) max = luminosity;
        }

        var amplitude = max - min;
        if (amplitude < 1)
        {
            throw new InvalidOperationException(
                $"Amplitude de luminosité quasi nulle sur la ligne d'analyse (min={min:F1}, max={max:F1}, " +
                $"amplitude={amplitude:F1}) : keyboard_height ne croise probablement pas le clavier. " +
                "Essaie une autre valeur (plage typique : 0.65–0.92 selon la vidéo).");
        }

        var whiteThreshold = min + WhiteThresholdRatio * amplitude;
        var blackThreshold = min + BlackThresholdRatio * amplitude;

        // Run-length encoding des pixels classés Blanc/Noir ; les pixels de la zone
        // ambiguë (entre les deux seuils) agissent comme séparateurs entre touches,
        // filtrant au passage les fines lignes de séparation entre touches blanches
        // adjacentes (B-C, E-F) grâce à la largeur minimale de 3px.
        var runs = new List<(bool IsWhite, int StartX, int Length)>();
        bool? currentClass = null;
        var runStart = 0;

        void FlushRun(int endXExclusive)
        {
            if (currentClass is { } isWhite)
            {
                var length = endXExclusive - runStart;
                if (length >= MinKeyWidthPx)
                {
                    runs.Add((isWhite, runStart, length));
                }
            }
        }

        for (var x = 0; x < pixelCount; x++)
        {
            bool? classification = luminosities[x] >= whiteThreshold ? true
                : luminosities[x] <= blackThreshold ? false
                : null;

            if (classification != currentClass)
            {
                FlushRun(x);
                currentClass = classification;
                runStart = x;
            }
        }
        FlushRun(pixelCount);

        if (runs.Count < 12)
        {
            throw new InvalidOperationException(
                $"Seulement {runs.Count} touche(s) détectée(s) sur la ligne d'analyse (min={min:F1}, max={max:F1}) : " +
                "vérifie keyboard_height (croise-t-il bien touches blanches ET noires ?) et threshold.");
        }

        var keys = runs
            .Select((run, index) => new KeyRegion(index, run.StartX, run.StartX + run.Length, run.IsWhite))
            .ToList();

        var middleCIndex = DetectMiddleCIndex(keys);

        logger.LogInformation(
            "Clavier détecté : {KeyCount} touches, do central à l'index {MiddleC}.",
            keys.Count, middleCIndex);

        return new KeyboardLayout(keys, middleCIndex);
    }

    public IEnumerable<NoteEvent> ExtractNoteEvents(
        KeyboardLayout layout,
        IEnumerable<PixelRow> rows,
        int threshold,
        bool separateHandsByColor)
    {
        var keys = layout.Keys;
        var pressed = new bool[keys.Count];
        var pressedChannel = new int[keys.Count];
        double[]? restLuminosities = null;

        // Buffers réutilisés à chaque frame. Important : toute lecture du ReadOnlySpan<byte>
        // (ref struct, ne peut pas survivre à un `yield return`) doit être terminée avant
        // le premier `yield return` de cette itération — d'où l'extraction dans une méthode
        // à part qui ne contient elle-même aucun yield.
        var frameLuminosities = new double[keys.Count];
        var frameChannels = new int[keys.Count];

        foreach (var frame in rows)
        {
            ComputeFrameKeyStats(frame.Rgb.Span, keys, separateHandsByColor, frameLuminosities, frameChannels);

            // La toute première frame sert de référence "touches relâchées" (brief : valeur
            // de repos), en supposant qu'aucune touche n'est enfoncée à l'image 0.
            restLuminosities ??= (double[])frameLuminosities.Clone();

            for (var i = 0; i < keys.Count; i++)
            {
                var isPressedNow = Math.Abs(frameLuminosities[i] - restLuminosities[i]) > threshold;
                if (isPressedNow == pressed[i])
                {
                    continue;
                }

                pressed[i] = isPressedNow;
                var midiNote = 60 + (i - layout.MiddleCIndex);

                if (isPressedNow)
                {
                    pressedChannel[i] = frameChannels[i];
                    yield return new NoteEvent(midiNote, frame.TimestampSeconds, true, 80, pressedChannel[i]);
                }
                else
                {
                    yield return new NoteEvent(midiNote, frame.TimestampSeconds, false, 0, pressedChannel[i]);
                }
            }
        }
    }

    public int EstimateBpm(IReadOnlyList<NoteEvent> noteEvents)
    {
        const int defaultBpm = 120;
        const int minBpm = 40;
        const int maxBpm = 240;

        var onsets = noteEvents
            .Where(e => e.IsNoteOn)
            .Select(e => e.TimestampSeconds)
            .OrderBy(t => t)
            .ToList();

        if (onsets.Count < 4)
        {
            return defaultBpm;
        }

        var intervals = new List<double>();
        for (var i = 1; i < onsets.Count; i++)
        {
            var interval = onsets[i] - onsets[i - 1];
            // Ignore les quasi-simultanéités (accords) qui fausseraient l'estimation.
            if (interval > 0.05)
            {
                intervals.Add(interval);
            }
        }

        if (intervals.Count < 4)
        {
            return defaultBpm;
        }

        intervals.Sort();
        var median = intervals[intervals.Count / 2];

        // Heuristique volontairement simple : on suppose que l'intervalle médian entre
        // attaques correspond à une double-croche (subdivision la plus courante dans un
        // morceau joué en continu). Fonctionnera mal sur des morceaux très syncopés ou
        // avec beaucoup de silences — à affiner une fois testé sur de vraies vidéos
        // (une vraie détection par autocorrélation serait plus robuste).
        var beatSeconds = median * 4;
        if (beatSeconds <= 0)
        {
            return defaultBpm;
        }

        var bpm = 60.0 / beatSeconds;

        // Correction d'erreur d'octave (bug réel observé le 2026-08-27) : l'hypothèse
        // "médiane = double-croche" ne tient pas pour un morceau dont la subdivision la
        // plus courante est la croche (accords sur le temps + basse en croches, cas très
        // fréquent) — le calcul brut sous-estime alors le tempo d'un facteur ~2 (un
        // morceau réellement à 75 BPM donnait ~37.5 BPM, clampé au plancher à 40 BPM).
        // Plutôt que de garder une valeur clampée qui n'a physiquement aucun sens pour un
        // morceau joué en continu, on "replie" le résultat vers la plage de tempo la plus
        // courante pour ce genre de morceaux (doublement/division par 2, l'erreur
        // classique en détection de tempo) jusqu'à y rentrer. Limite connue : un morceau
        // réellement très lent (<70 BPM) ou très rapide (>150 BPM) sera mal replié — une
        // vraie détection par autocorrélation resterait plus robuste à terme.
        const double typicalMinBpm = 70.0;
        const double typicalMaxBpm = 150.0;
        var foldGuard = 0;
        while (bpm > 0 && bpm < typicalMinBpm && foldGuard < 4)
        {
            bpm *= 2;
            foldGuard++;
        }
        foldGuard = 0;
        while (bpm > typicalMaxBpm && foldGuard < 4)
        {
            bpm /= 2;
            foldGuard++;
        }

        return Math.Clamp((int)Math.Round(bpm), minBpm, maxBpm);
    }

    /// Motif [W,W,B,W,B,W,W] (7 touches) : l'élément d'index 1 de chaque fenêtre
    /// correspondante est un do. Ce motif est unique dans l'octave (transition B→C) —
    /// la transition E→F, seule autre paire de touches blanches adjacentes, produit
    /// [W,W,B,W,B,W,B] (ne diffère qu'au 7ᵉ élément), d'où la fenêtre de 7 touches.
    private static int DetectMiddleCIndex(IReadOnlyList<KeyRegion> keys)
    {
        ReadOnlySpan<bool> pattern = [true, true, false, true, false, true, true];
        var candidates = new List<int>();

        for (var i = 0; i <= keys.Count - pattern.Length; i++)
        {
            var matches = true;
            for (var p = 0; p < pattern.Length; p++)
            {
                if (keys[i + p].IsWhite != pattern[p])
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                candidates.Add(i + 1); // élément d'index 1 de la fenêtre = do
            }
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "Aucun do détecté (motif [W,W,B,W,B,W,W] introuvable) : la détection des touches a échoué.");
        }

        candidates.Sort();
        return candidates[candidates.Count / 2]; // do médian
    }

    private static void ComputeFrameKeyStats(
        ReadOnlySpan<byte> row,
        IReadOnlyList<KeyRegion> keys,
        bool separateHandsByColor,
        double[] luminosities,
        int[] channels)
    {
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            double sumLum = 0;
            long sumG = 0, sumB = 0;
            var count = 0;

            for (var x = key.StartX; x < key.EndX; x++)
            {
                var r = row[x * 3];
                var g = row[x * 3 + 1];
                var b = row[x * 3 + 2];
                sumLum += 0.299 * r + 0.587 * g + 0.114 * b;
                sumG += g;
                sumB += b;
                count++;
            }

            luminosities[i] = count > 0 ? sumLum / count : 0;

            // Heuristique couleur Synthesia : main droite typiquement verte, main gauche
            // typiquement bleue. Les couleurs varient selon le logiciel source — à ajuster
            // une fois testé sur de vraies vidéos si SeparateHandsByColor donne de mauvais
            // résultats (idée : rendre les 2 couleurs de référence configurables).
            channels[i] = separateHandsByColor && count > 0 && sumB > sumG ? 1 : 0;
        }
    }

    private static double Luminosity(ReadOnlySpan<byte> row, int x)
    {
        var r = row[x * 3];
        var g = row[x * 3 + 1];
        var b = row[x * 3 + 2];
        return 0.299 * r + 0.587 * g + 0.114 * b;
    }
}
