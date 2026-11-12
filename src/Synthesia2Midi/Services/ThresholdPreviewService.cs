using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Synthesia2Midi.Services.Abstractions;

namespace Synthesia2Midi.Services;

/// <summary>
/// Prévisualisation des réglages sur la page /nouvelle, avant toute conversion :
/// récupère un court extrait de la vidéo à partir de « Début », en tire l'image de cet
/// instant, puis relit la ligne du clavier exactement comme le pipeline (même décodeur,
/// même DetectLayout, même référence « repos » = médiane de chaque touche). Le résultat est la
/// matrice des écarts de luminosité par image et par touche : n'importe quel seuil
/// s'évalue ensuite dessus sans rien relancer.
/// </summary>
public partial class ThresholdPreviewService(
    IServiceScopeFactory scopeFactory,
    ILogger<ThresholdPreviewService> logger)
{
    public const double ExtractSeconds = 10;
    private const int HandSampleMinDiff = 30; // écart de luminosité d'une touche nettement allumée

    // Même flux vidéo que la conversion (YtDlpVideoAcquisitionService), sans l'audio.
    private const string VideoOnlyFormat =
        "bv*[height<=720][vcodec^=avc1]/bv*[height<=720][ext=mp4]/b[height<=720]/b";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    private static string PreviewRoot =>
        Path.Combine(Environment.GetEnvironmentVariable("DATA_DIR") ?? "/data", "preview");

    /// Extrait (téléchargé ou fichier local) prêt à être relu, mis en cache par source + début.
    public async Task<PreviewClip> GetClipAsync(string source, bool isUrl, double startSeconds, CancellationToken ct)
    {
        startSeconds = Math.Max(0, Math.Round(startSeconds, 1));
        var key = CacheKey(source, startSeconds);
        var dir = Path.Combine(PreviewRoot, key);
        var framePath = Path.Combine(dir, "frame.jpg");

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            CleanupStaleEntries(key);

            string videoPath;
            TimeSpan? decodeStart;
            TimeSpan? decodeEnd;

            if (isUrl)
            {
                videoPath = FindClip(dir) ?? await DownloadClipAsync(source, startSeconds, dir, ct);
                decodeStart = null; // l'extrait commence déjà à « Début »
                decodeEnd = null;
            }
            else
            {
                if (!File.Exists(source))
                {
                    throw new PreviewException("Le fichier vidéo envoyé est introuvable sur le serveur.");
                }
                Directory.CreateDirectory(dir);
                videoPath = source;
                decodeStart = TimeSpan.FromSeconds(startSeconds);
                decodeEnd = TimeSpan.FromSeconds(startSeconds + ExtractSeconds);
            }

            if (!File.Exists(framePath))
            {
                await ExtractFrameAsync(videoPath, isUrl ? null : startSeconds, framePath, ct);
            }

            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);
            return new PreviewClip(key, videoPath, decodeStart, decodeEnd, startSeconds,
                File.Exists(framePath) ? $"/api/preview/{key}/frame.jpg" : null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// Relit la ligne du clavier sur l'extrait et renvoie les écarts à la référence « repos ».
    public async Task<ThresholdProbe> AnalyzeAsync(PreviewClip clip, double keyboardHeight, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var decoder = scope.ServiceProvider.GetRequiredService<IFrameDecoderService>();
        var analysis = scope.ServiceProvider.GetRequiredService<IKeyboardAnalysisService>();

        var rows = new List<PixelRow>();
        await foreach (var row in decoder.ReadKeyboardRowsAsync(clip.VideoPath, keyboardHeight, clip.DecodeStart, clip.DecodeEnd, ct))
        {
            rows.Add(row);
        }

        if (rows.Count < 2)
        {
            throw new PreviewException("Aucune image n'a pu être lue dans l'extrait. Le début est peut-être après la fin de la vidéo.");
        }

        KeyboardLayout layout;
        try
        {
            layout = analysis.DetectLayout(rows[0].Rgb.Span);
        }
        catch (InvalidOperationException)
        {
            throw new PreviewException(
                "Le clavier n'est pas reconnu sur la ligne choisie, à l'instant du début. " +
                "Placez la ligne sur la rangée des touches noires, ou choisissez un début où le clavier est entièrement visible.",
                isLayout: true);
        }

        var keys = layout.Keys;
        var frames = rows.Count;
        var diffs = new byte[frames * keys.Count];
        var hands = new byte[frames * keys.Count];
        var hues = new double[frames * keys.Count];
        var saturations = new double[frames * keys.Count];
        var luminosities = new float[frames * keys.Count];

        for (var f = 0; f < frames; f++)
        {
            var rgb = rows[f].Rgb.Span;
            for (var k = 0; k < keys.Count; k++)
            {
                var key = keys[k];
                double sum = 0;
                long sumR = 0, sumG = 0, sumB = 0;
                var count = 0;
                for (var x = key.StartX; x < key.EndX; x++)
                {
                    var r = rgb[x * 3];
                    var g = rgb[x * 3 + 1];
                    var b = rgb[x * 3 + 2];
                    sum += 0.299 * r + 0.587 * g + 0.114 * b;
                    sumR += r;
                    sumG += g;
                    sumB += b;
                    count++;
                }

                luminosities[f * keys.Count + k] = (float)(count > 0 ? sum / count : 0);
                (hues[f * keys.Count + k], saturations[f * keys.Count + k]) = count > 0
                    ? KeyboardAnalysisService.HueSaturation(sumR / (double)count, sumG / (double)count, sumB / (double)count)
                    : (double.NaN, 0);
            }
        }

        // Même référence « repos » que ExtractNoteEvents : médiane de chaque touche sur l'extrait
        // (bug #18 corrigé le 2026-11-12, avant : la première image).
        var rest = KeyboardAnalysisService.RestLuminosities(luminosities, frames, keys.Count);
        for (var i = 0; i < diffs.Length; i++)
        {
            // ceil : « écart > seuil » (seuil entier) donne le même résultat sur l'octet arrondi
            // que sur la valeur réelle (à l'arrondi float près).
            diffs[i] = (byte)Math.Min(255, Math.Ceiling(Math.Abs(luminosities[i] - rest[i % keys.Count])));
        }

        // Mains : même méthode que la conversion (HandAssigner, deux familles de couleurs). Les
        // échantillons sont les touches nettement allumées (écart ≥ HandSampleMinDiff) ; l'index
        // de la touche sert de hauteur (seul l'ordre grave → aigu compte).
        var samples = new List<(double, double, double)>();
        for (var i = 0; i < diffs.Length; i++)
        {
            if (diffs[i] >= HandSampleMinDiff)
            {
                samples.Add((hues[i], saturations[i], i % keys.Count));
            }
        }

        var handColors = HandAssigner.FindHandColors(samples, out _);
        for (var i = 0; i < hands.Length; i++)
        {
            hands[i] = (byte)(handColors?.HandOf(hues[i], saturations[i], i % keys.Count) ?? 0);
        }

        var duration = rows[^1].TimestampSeconds;
        var fps = duration > 0 ? (frames - 1) / duration : 30;

        return new ThresholdProbe(
            frames,
            fps,
            keys.Count,
            layout.MiddleCIndex,
            keys.Select(k => k.IsWhite).ToArray(),
            diffs,
            hands);
    }

    /// Chemin de l'image de l'instant « Début » pour une clé de cache, ou null.
    public string? GetFramePath(string key)
    {
        if (!KeyPattern().IsMatch(key))
        {
            return null;
        }
        var path = Path.Combine(PreviewRoot, key, "frame.jpg");
        return File.Exists(path) ? path : null;
    }

    private async Task<string> DownloadClipAsync(string url, double startSeconds, string dir, CancellationToken ct)
    {
        var tmp = dir + ".part-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(tmp);

        var section = string.Create(CultureInfo.InvariantCulture,
            $"*{startSeconds:0.0}-{startSeconds + ExtractSeconds:0.0}");

        var psi = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[]
                 {
                     "--no-warnings", "--no-playlist", "--no-progress",
                     "-f", VideoOnlyFormat,
                     "--download-sections", section,
                     "-o", Path.Combine(tmp, "clip.%(ext)s"),
                     url,
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        logger.LogInformation("Aperçu : extrait {Section} de {Url}", section, url);

        var stderr = new StringBuilder();
        using var process = Process.Start(psi) ?? throw new PreviewException("Impossible de démarrer yt-dlp.");
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                lock (stderr) { stderr.AppendLine(e.Data); }
            }
        };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            TryDeleteDirectory(tmp);
            throw;
        }

        var clip = FindClip(tmp);
        if (process.ExitCode != 0 || clip is null)
        {
            TryDeleteDirectory(tmp);
            string detail;
            lock (stderr) { detail = stderr.ToString().Trim(); }
            logger.LogWarning("Aperçu : yt-dlp a échoué (code {Code}) : {Stderr}", process.ExitCode, detail);
            throw new PreviewException("Impossible de récupérer l'extrait depuis YouTube. Réessayez dans un instant.");
        }

        // Le dossier final n'apparaît qu'une fois l'extrait complet.
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.GetFiles(tmp))
            {
                File.Move(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
            }
            TryDeleteDirectory(tmp);
        }
        else
        {
            Directory.Move(tmp, dir);
        }

        return FindClip(dir) ?? throw new PreviewException("L'extrait récupéré est introuvable.");
    }

    private async Task ExtractFrameAsync(string videoPath, double? seekSeconds, string framePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        if (seekSeconds is double s)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(s.ToString("0.###", CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        foreach (var arg in new[] { "-frames:v", "1", "-vf", "scale='min(1280,iw)':-2", "-q:v", "4", framePath })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            return;
        }
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            // Sans image, l'aperçu garde la miniature YouTube : pas bloquant.
            logger.LogWarning("Aperçu : image de début non extraite : {Stderr}", await stderrTask);
        }
    }

    private void CleanupStaleEntries(string keepKey)
    {
        try
        {
            if (!Directory.Exists(PreviewRoot))
            {
                return;
            }
            var limit = DateTime.UtcNow - CacheLifetime;
            foreach (var entry in Directory.GetDirectories(PreviewRoot))
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith(keepKey, StringComparison.Ordinal))
                {
                    continue;
                }
                if (Directory.GetLastWriteTimeUtc(entry) < limit)
                {
                    TryDeleteDirectory(entry);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Aperçu : nettoyage du cache incomplet.");
        }
    }

    private static string? FindClip(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, "clip.*").FirstOrDefault(f => !f.EndsWith(".part", StringComparison.Ordinal))
            : null;

    private static string CacheKey(string source, double startSeconds)
    {
        var raw = $"{source.Trim()}|{startSeconds.ToString("0.0", CultureInfo.InvariantCulture)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..24].ToLowerInvariant();
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { }
    }

    // Uniquement des dossiers du cache d'aperçu (/data/preview), jamais de données utilisateur.
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Path.GetFullPath(path).StartsWith(Path.GetFullPath(PreviewRoot), StringComparison.Ordinal))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch { }
    }

    [GeneratedRegex("^[a-f0-9]{24}$")]
    private static partial Regex KeyPattern();
}

public record PreviewClip(
    string Key,
    string VideoPath,
    TimeSpan? DecodeStart,
    TimeSpan? DecodeEnd,
    double StartSeconds,
    string? FrameUrl);

/// Écarts de luminosité par image et par touche (Diffs[frame * KeyCount + key]),
/// et main (Hands, 1 = main gauche, 0 = main droite, d'après les deux couleurs de la vidéo) au même indice.
public record ThresholdProbe(
    int Frames,
    double Fps,
    int KeyCount,
    int MiddleCIndex,
    bool[] IsWhite,
    byte[] Diffs,
    byte[] Hands)
{
    public double DurationSeconds => Fps > 0 ? Frames / Fps : 0;

    /// Rejoue la machine à états de ExtractNoteEvents pour un seuil donné.
    public ProbeStats Evaluate(int threshold)
    {
        var notes = 0;
        var shortNotes = 0;
        var keysUsed = 0;
        var shortFrames = Math.Max(2, (int)Math.Round(Fps * 0.08));

        for (var k = 0; k < KeyCount; k++)
        {
            var pressed = false;
            var onset = 0;
            var used = false;
            for (var f = 0; f < Frames; f++)
            {
                var now = Diffs[f * KeyCount + k] > threshold;
                if (now == pressed)
                {
                    continue;
                }
                pressed = now;
                if (now)
                {
                    onset = f;
                    notes++;
                    used = true;
                }
                else if (f - onset < shortFrames)
                {
                    shortNotes++;
                }
            }
            if (used)
            {
                keysUsed++;
            }
        }

        return new ProbeStats(notes, shortNotes, keysUsed, DurationSeconds);
    }
}

public record ProbeStats(int Notes, int ShortNotes, int KeysUsed, double DurationSeconds)
{
    public double PerSecond => DurationSeconds > 0 ? Notes / DurationSeconds : 0;
    public double ShortShare => Notes > 0 ? (double)ShortNotes / Notes : 0;
}

public class PreviewException(string message, bool isLayout = false) : Exception(message)
{
    public bool IsLayout { get; } = isLayout;
}
