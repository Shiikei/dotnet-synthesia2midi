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

    /// <summary>
    /// Tempo du morceau en BPM (décimal : un tempo arrondi à l'entier fait dériver la grille
    /// sur plusieurs minutes). 2026-11-11 : on cherche d'abord le tempo dont la grille de
    /// doubles-croches colle le mieux aux attaques (voir EstimateBpmByGrid) ; l'ancienne
    /// heuristique « intervalle médian = double-croche » ne sert plus qu'en secours, quand
    /// aucune grille ne ressort (jeu humain, rubato). Cas réel : Sunflower donnait 106 BPM au
    /// lieu de 88 (68 mesures au lieu de 57).
    /// </summary>
    public double EstimateBpm(IReadOnlyList<NoteEvent> noteEvents)
    {
        var onsetTimes = noteEvents.Where(e => e.IsNoteOn).Select(e => e.TimestampSeconds).ToList();
        var byGrid = EstimateBpmByGrid(onsetTimes);
        if (byGrid is double gridBpm)
        {
            logger.LogInformation("Tempo détecté par alignement de grille : {Bpm:F2} BPM.", gridBpm);
            return gridBpm;
        }

        var fallback = EstimateBpmByMedianInterval(noteEvents);
        logger.LogInformation("Aucune grille nette : tempo estimé par l'intervalle médian, {Bpm} BPM.", fallback);
        return fallback;
    }

    /// <summary>
    /// Les vidéos Synthesia sont générées par ordinateur : les attaques tombent exactement sur une
    /// grille régulière. Pour chaque tempo candidat (40–200 BPM, pas 0,1), on mesure à quel point
    /// les attaques s'alignent sur sa grille de doubles-croches (longueur du vecteur moyen des
    /// phases, 0 = aléatoire, 1 = parfait). Le bon tempo donne un pic net (75 → 0,89 sur Kimetsu,
    /// 88 → 0,70 sur Sunflower) ; les tempos voisins décrochent vite car l'erreur s'accumule sur
    /// toute la durée. Un tempo deux fois plus lent qui s'aligne presque aussi bien est préféré
    /// (une grille alignée l'est aussi à tempo double). Sous MinGridAlignment : null (pas de grille).
    /// </summary>
    internal static double? EstimateBpmByGrid(IReadOnlyList<double> onsetTimes)
    {
        const double minCandidate = 40.0;
        const double maxCandidate = 200.0;
        const double coarseStep = 0.1;
        const double minGridAlignment = 0.35;
        const double halfTempoTolerance = 0.85;

        // Attaques dédoublonnées (les notes d'un accord tombent sur la même image).
        var onsets = new List<double>();
        foreach (var t in onsetTimes.OrderBy(t => t))
        {
            if (onsets.Count == 0 || t - onsets[^1] > 0.03)
            {
                onsets.Add(t);
            }
        }

        if (onsets.Count < 16)
        {
            return null;
        }

        double Alignment(double bpm)
        {
            var period = 60.0 / bpm / 4.0;
            double sin = 0, cos = 0;
            foreach (var t in onsets)
            {
                var angle = 2 * Math.PI * t / period;
                sin += Math.Sin(angle);
                cos += Math.Cos(angle);
            }

            return Math.Sqrt(sin * sin + cos * cos) / onsets.Count;
        }

        var bestBpm = 0.0;
        var bestScore = -1.0;
        for (var bpm = minCandidate; bpm <= maxCandidate + 1e-9; bpm += coarseStep)
        {
            var score = Alignment(bpm);
            if (score > bestScore)
            {
                bestScore = score;
                bestBpm = bpm;
            }
        }

        if (bestScore < minGridAlignment)
        {
            return null;
        }

        // Préférer la moitié du tempo si elle s'aligne presque aussi bien (erreur d'octave).
        while (bestBpm / 2 >= minCandidate)
        {
            var half = Refine(bestBpm / 2);
            if (half.Score < bestScore * halfTempoTolerance)
            {
                break;
            }

            (bestBpm, bestScore) = (half.Bpm, half.Score);
        }

        return Math.Round(Refine(bestBpm).Bpm, 2);

        // Affinage autour d'un candidat (±0,2 BPM, pas 0,01).
        (double Bpm, double Score) Refine(double center)
        {
            var bpm = center;
            var score = Alignment(center);
            for (var b = center - 0.2; b <= center + 0.2 + 1e-9; b += 0.01)
            {
                var s = Alignment(b);
                if (s > score)
                {
                    (bpm, score) = (b, s);
                }
            }

            return (bpm, score);
        }
    }

    private static int EstimateBpmByMedianInterval(IReadOnlyList<NoteEvent> noteEvents)
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
