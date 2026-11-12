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

        // 1) Méthode historique : une région par segment blanc/noir, do repéré par le motif
        //    [W,W,B,W,B,W,W]. Gardée telle quelle quand le résultat est cohérent (alternance
        //    blanc/noir conforme à un clavier réel sur toute la ligne) : aucun changement
        //    pour les vidéos qui fonctionnaient déjà (références Kimetsu, Sunflower).
        List<KeyRegion>? legacyKeys = null;
        int? legacyMiddleC = null;
        if (runs.Count >= 12)
        {
            legacyKeys = runs
                .Select((run, index) => new KeyRegion(index, run.StartX, run.StartX + run.Length, run.IsWhite))
                .ToList();
            legacyMiddleC = TryDetectMiddleCIndex(legacyKeys);

            if (legacyMiddleC is int mc && IsChromaticallyConsistent(legacyKeys, mc))
            {
                logger.LogInformation(
                    "Clavier détecté : {KeyCount} touches, do central à l'index {MiddleC}.",
                    legacyKeys.Count, mc);
                return new KeyboardLayout(legacyKeys, mc);
            }
        }

        // 2) Repérage par les touches noires (2026-11-11, cas « Shinobu Kocho Theme ») : sur un
        //    clavier de 88 touches fines, la ligne entre deux blanches voisines (si-do, mi-fa)
        //    est trop pâle et deux blanches fusionnent en un seul segment. Les noires, elles,
        //    restent nettes : leurs groupes de 2 et 3 suffisent à reconstruire tout le clavier.
        if (TryBuildFromBlackKeys(runs, pixelCount, out var rebuiltKeys, out var rebuiltMiddleC, out var reason))
        {
            logger.LogInformation(
                "Clavier reconstruit d'après les touches noires : {KeyCount} touches, do central à l'index {MiddleC}.",
                rebuiltKeys.Count, rebuiltMiddleC);
            return new KeyboardLayout(rebuiltKeys, rebuiltMiddleC);
        }

        // 3) Dernier recours : résultat historique même incohérent (comportement d'avant).
        if (legacyKeys is not null && legacyMiddleC is int fallbackMiddleC)
        {
            logger.LogWarning(
                "Clavier détecté avec une alternance blanc/noir incohérente ({KeyCount} touches) ; " +
                "repérage par les touches noires impossible ({Reason}).",
                legacyKeys.Count, reason);
            return new KeyboardLayout(legacyKeys, fallbackMiddleC);
        }

        if (runs.Count < 12)
        {
            throw new InvalidOperationException(
                $"Seulement {runs.Count} touche(s) détectée(s) sur la ligne d'analyse (min={min:F1}, max={max:F1}) : " +
                "vérifie keyboard_height (croise-t-il bien touches blanches ET noires ?) et threshold.");
        }

        throw new InvalidOperationException(
            $"Aucun do détecté (motif [W,W,B,W,B,W,W] introuvable, repérage par les touches noires : {reason}) : " +
            "la détection des touches a échoué.");
    }

    /// Classes de hauteur des touches blanches (do = 0).
    private static bool IsWhitePitchClass(int semitone) => ((semitone % 12) + 12) % 12 is 0 or 2 or 4 or 5 or 7 or 9 or 11;

    /// Vrai si chaque touche a la couleur attendue d'après sa distance au do central.
    private static bool IsChromaticallyConsistent(IReadOnlyList<KeyRegion> keys, int middleCIndex)
    {
        for (var i = 0; i < keys.Count; i++)
        {
            if (keys[i].IsWhite != IsWhitePitchClass(i - middleCIndex))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Reconstruit le clavier complet à partir des seules touches noires de la ligne :
    /// 1. les noires sont régulièrement espacées par demi-ton, l'écart entre deux noires voisines
    ///    vaut donc 2 demi-tons (dans un groupe) ou 3 (entre deux groupes, mi-fa ou si-do) ;
    /// 2. les groupes alternent 2 noires (do#, ré#) et 3 noires (fa#, sol#, la#), ce qui nomme
    ///    chaque noire ;
    /// 3. les blanches sont placées entre les noires (1 ou 2 selon l'écart) et aux deux bouts,
    ///    d'après la largeur d'une blanche (12 demi-tons = 7 blanches).
    /// Échoue (reason) dès qu'un écart ou un groupe ne colle pas (noire masquée, effet lumineux…).
    /// </summary>
    private static bool TryBuildFromBlackKeys(
        IReadOnlyList<(bool IsWhite, int StartX, int Length)> runs,
        int pixelCount,
        out List<KeyRegion> keys,
        out int middleCIndex,
        out string reason)
    {
        keys = [];
        middleCIndex = 0;

        // Noires entières uniquement (un segment coupé par le bord de l'image n'est pas fiable).
        var blacks = runs
            .Where(r => !r.IsWhite && r.StartX > 0 && r.StartX + r.Length < pixelCount)
            .Select(r => (Start: r.StartX, End: r.StartX + r.Length))
            .ToList();
        if (blacks.Count >= 5)
        {
            var medianWidth = Median(blacks.Select(b => (double)(b.End - b.Start)));
            blacks = blacks
                .Where(b => b.End - b.Start >= 0.5 * medianWidth && b.End - b.Start <= 1.6 * medianWidth)
                .ToList();
        }
        if (blacks.Count < 5)
        {
            reason = $"{blacks.Count} touche(s) noire(s) exploitable(s)";
            return false;
        }

        var centers = blacks.Select(b => (b.Start + b.End) / 2.0).ToList();
        var gaps = centers.Zip(centers.Skip(1), (a, b) => b - a).ToList();
        var medianGap = Median(gaps);
        var semitoneWidth = Median(gaps.Where(g => g < 1.25 * medianGap)) / 2;

        var steps = new List<int>(gaps.Count);
        foreach (var gap in gaps)
        {
            var ratio = gap / semitoneWidth;
            var step = (int)Math.Round(ratio);
            if (step is not (2 or 3) || Math.Abs(ratio - step) > 0.3)
            {
                reason = $"écart de {ratio:F2} demi-tons entre deux touches noires";
                return false;
            }
            steps.Add(step);
        }

        // Groupes de noires séparés par les écarts de 3 demi-tons.
        var groupSizes = new List<int> { 1 };
        foreach (var step in steps)
        {
            if (step == 3) groupSizes.Add(1);
            else groupSizes[^1]++;
        }

        var inner = groupSizes.Skip(1).Take(groupSizes.Count - 2).ToList();
        if (inner.Count == 0)
        {
            reason = "clavier trop court (moins d'un groupe complet de touches noires)";
            return false;
        }
        if (groupSizes.Any(s => s > 3)
            || inner.Any(s => s is not (2 or 3))
            || inner.Zip(inner.Skip(1), (a, b) => a + b).Any(sum => sum != 5))
        {
            reason = $"groupes de touches noires incohérents ({string.Join("-", groupSizes)})";
            return false;
        }

        // Le premier groupe (éventuellement incomplet) est du type opposé au suivant.
        var firstGroupType = 5 - inner[0];
        if (groupSizes[0] > firstGroupType)
        {
            reason = $"groupes de touches noires incohérents ({string.Join("-", groupSizes)})";
            return false;
        }
        int[] groupPitchClasses = firstGroupType == 2 ? [1, 3] : [6, 8, 10];
        var firstBlackPitchClass = groupPitchClasses[groupPitchClasses.Length - groupSizes[0]];

        // Demi-tons absolus (origine arbitraire positive, seules les différences comptent).
        var semitones = new List<int>(blacks.Count) { 120 + firstBlackPitchClass };
        foreach (var step in steps)
        {
            semitones.Add(semitones[^1] + step);
        }

        // Étendue du clavier = premier et dernier segment blanc.
        var whiteRuns = runs.Where(r => r.IsWhite).ToList();
        var left = whiteRuns.Count > 0 ? whiteRuns[0].StartX : 0;
        var right = whiteRuns.Count > 0 ? whiteRuns[^1].StartX + whiteRuns[^1].Length : pixelCount;
        var whiteWidth = 12 * semitoneWidth / 7;

        // Blanches au-delà de la première / dernière noire : autant que la place le permet,
        // sans jamais franchir une noire (absente car hors image).
        int EdgeWhiteCount(double distance, int fromSemitone, int direction)
        {
            var wanted = Math.Max(0, (int)Math.Round(distance / whiteWidth));
            var count = 0;
            var semitone = fromSemitone;
            while (count < wanted)
            {
                semitone += direction;
                if (!IsWhitePitchClass(semitone)) break;
                count++;
            }
            return count;
        }

        var leftCount = EdgeWhiteCount(centers[0] - left, semitones[0], -1);
        var rightCount = EdgeWhiteCount(right - centers[^1], semitones[^1], +1);

        var regions = new List<(int Start, int End, bool IsWhite)>();
        void AddWhites(double from, double to, int count)
        {
            for (var j = 0; j < count; j++)
            {
                var x0 = from + (to - from) * j / count;
                var x1 = from + (to - from) * (j + 1) / count;
                var margin = x1 - x0 > 6 ? 1 : 0; // évite la fine ligne de séparation
                regions.Add(((int)Math.Round(x0) + margin, (int)Math.Round(x1) - margin, true));
            }
        }

        AddWhites(left, blacks[0].Start, leftCount);
        for (var i = 0; i < blacks.Count; i++)
        {
            regions.Add((blacks[i].Start, blacks[i].End, false));
            if (i + 1 < blacks.Count)
            {
                AddWhites(blacks[i].End, blacks[i + 1].Start, semitones[i + 1] - semitones[i] - 1);
            }
        }
        AddWhites(blacks[^1].End, right, rightCount);

        keys = regions
            .Select((r, index) => new KeyRegion(index, r.Start, Math.Max(r.End, r.Start + 1), r.IsWhite))
            .ToList();

        if (TryDetectMiddleCIndex(keys) is not int middleC)
        {
            reason = "aucun do complet dans le clavier reconstruit";
            return false;
        }

        middleCIndex = middleC;
        reason = string.Empty;
        return true;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    public IEnumerable<NoteEvent> ExtractNoteEvents(
        KeyboardLayout layout,
        IEnumerable<PixelRow> rows,
        int threshold,
        bool separateHandsByColor)
    {
        var keys = layout.Keys;
        var pressed = new bool[keys.Count];
        var frames = rows as IReadOnlyList<PixelRow> ?? rows.ToList();

        // Buffers réutilisés à chaque frame. Important : toute lecture du ReadOnlySpan<byte>
        // (ref struct, ne peut pas survivre à un `yield return`) doit être terminée avant
        // le premier `yield return` de cette itération — d'où l'extraction dans une méthode
        // à part qui ne contient elle-même aucun yield.
        var frameLuminosities = new double[keys.Count];
        var frameHues = new double[keys.Count];
        var frameSaturations = new double[keys.Count];

        // Passe 1 : luminosité de chaque touche sur toutes les images, pour la référence
        // « touche relâchée » (voir RestLuminosities). 2026-11-12, bug #18 : avant, la référence
        // était la seule première image ; une touche déjà allumée à cet instant restait fausse
        // pendant toute la vidéo (ses vraies notes passaient inaperçues, ses relâchements
        // devenaient des notes).
        var allLuminosities = new float[frames.Count * keys.Count];
        for (var f = 0; f < frames.Count; f++)
        {
            ComputeFrameKeyStats(frames[f].Rgb.Span, keys, frameLuminosities, frameHues, frameSaturations);
            for (var i = 0; i < keys.Count; i++)
            {
                allLuminosities[f * keys.Count + i] = (float)frameLuminosities[i];
            }
        }

        var restLuminosities = RestLuminosities(allLuminosities, frames.Count, keys.Count);

        // Passe 2 : machine à états touche par touche.
        foreach (var frame in frames)
        {
            ComputeFrameKeyStats(frame.Rgb.Span, keys, frameLuminosities, frameHues, frameSaturations);

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
                    // Main attribuée après coup d'après la couleur (HandAssigner) : canal 0 ici.
                    yield return new NoteEvent(midiNote, frame.TimestampSeconds, true, 80, 0,
                        frameHues[i], frameSaturations[i]);
                }
                else
                {
                    yield return new NoteEvent(midiNote, frame.TimestampSeconds, false, 0, 0);
                }
            }
        }
    }

    /// <summary>
    /// Référence « touche relâchée » de chaque touche : la médiane de sa luminosité sur toutes
    /// les images. Une touche est relâchée la plus grande partie du morceau, donc la médiane tombe
    /// sur sa couleur de repos même si elle est allumée au tout début (bug #18, 2026-11-12).
    /// Partagée avec ThresholdPreviewService pour que le test du seuil reste fidèle à la conversion.
    /// luminosities : [image × keyCount + touche].
    /// </summary>
    internal static double[] RestLuminosities(float[] luminosities, int frames, int keyCount)
    {
        var rest = new double[keyCount];
        if (frames == 0)
        {
            return rest;
        }

        var column = new float[frames];
        for (var k = 0; k < keyCount; k++)
        {
            for (var f = 0; f < frames; f++)
            {
                column[f] = luminosities[f * keyCount + k];
            }

            Array.Sort(column);
            var mid = frames / 2;
            rest[k] = frames % 2 == 1 ? column[mid] : (column[mid - 1] + column[mid]) / 2.0;
        }

        return rest;
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
    /// Null si aucun do n'est trouvé.
    private static int? TryDetectMiddleCIndex(IReadOnlyList<KeyRegion> keys)
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
            return null;
        }

        candidates.Sort();
        return candidates[candidates.Count / 2]; // do médian
    }

    private static void ComputeFrameKeyStats(
        ReadOnlySpan<byte> row,
        IReadOnlyList<KeyRegion> keys,
        double[] luminosities,
        double[] hues,
        double[] saturations)
    {
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            double sumLum = 0;
            long sumR = 0, sumG = 0, sumB = 0;
            var count = 0;

            for (var x = key.StartX; x < key.EndX; x++)
            {
                var r = row[x * 3];
                var g = row[x * 3 + 1];
                var b = row[x * 3 + 2];
                sumLum += 0.299 * r + 0.587 * g + 0.114 * b;
                sumR += r;
                sumG += g;
                sumB += b;
                count++;
            }

            luminosities[i] = count > 0 ? sumLum / count : 0;

            // Couleur moyenne de la touche (teinte / saturation), utilisée par HandAssigner pour
            // reconnaître les deux couleurs de la vidéo, quelles qu'elles soient (2026-11-11 :
            // Sunflower est en bleu et rose, l'ancienne règle « bleu > vert = main gauche » ne
            // séparait rien).
            (hues[i], saturations[i]) = count > 0
                ? HueSaturation(sumR / (double)count, sumG / (double)count, sumB / (double)count)
                : (double.NaN, 0);
        }
    }

    internal static (double Hue, double Saturation) HueSaturation(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        if (max <= 0 || delta <= 0)
        {
            return (double.NaN, 0);
        }

        double hue;
        if (max == r) hue = 60 * (((g - b) / delta) % 6);
        else if (max == g) hue = 60 * ((b - r) / delta + 2);
        else hue = 60 * ((r - g) / delta + 4);

        return ((hue + 360) % 360, delta / max);
    }

    private static double Luminosity(ReadOnlySpan<byte> row, int x)
    {
        var r = row[x * 3];
        var g = row[x * 3 + 1];
        var b = row[x * 3 + 2];
        return 0.299 * r + 0.587 * g + 0.114 * b;
    }
}
