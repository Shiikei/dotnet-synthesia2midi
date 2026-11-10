using System.Diagnostics;
using Synthesia2Midi.Services.Abstractions;

namespace Synthesia2Midi.Services;

/// <summary>
/// Implémentation complète : reprend exactement la commande yt-dlp validée dans
/// la version Python (brief §"Fix important déjà appliqué"), avec repli mp4/H.264.
/// </summary>
public class YtDlpVideoAcquisitionService(ILogger<YtDlpVideoAcquisitionService> logger) : IVideoAcquisitionService
{
    // AV1 : YouTube sert souvent de l'AV1 que le décodeur ffmpeg-pipe (§ci-après)
    // ne gère pas de façon fiable ⇒ on force H.264, avec repli mp4 générique.
    private const string FormatSelector =
        "bv*[height<=720][vcodec^=avc1]+ba[ext=m4a]/bv*[height<=720][ext=mp4]+ba/b[height<=720]/b";

    public async Task<VideoAcquisitionResult> AcquireFromUrlAsync(string url, string destinationDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        var outputTemplate = Path.Combine(destinationDirectory, "%(id)s.%(ext)s");

        var psi = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(FormatSelector);
        psi.ArgumentList.Add("--recode-video");
        psi.ArgumentList.Add("mp4"); // filet de sécurité si le format choisi n'est pas déjà mp4
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputTemplate);
        // Deux --print marqués par un préfixe littéral pour pouvoir les distinguer sans
        // ambiguïté à la lecture des lignes stdout, quel que soit l'ordre réel dans lequel
        // yt-dlp les émet (before_dl a lieu avant le téléchargement, after_move après) :
        // le titre pour l'affichage dans /historique (brief "titre vidéo plutôt que l'URL"),
        // et le chemin final du fichier (déjà présent avant ce changement).
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("before_dl:TITLE::%(title)s");
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("after_move:PATH::%(filepath)s");
        psi.ArgumentList.Add(url);

        logger.LogInformation("yt-dlp: téléchargement de {Url}", url);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Impossible de démarrer le process yt-dlp.");

        string? filePath = null;
        string? title = null;
        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            var line = e.Data.Trim();
            if (line.StartsWith("TITLE::", StringComparison.Ordinal))
            {
                title = line["TITLE::".Length..];
            }
            else if (line.StartsWith("PATH::", StringComparison.Ordinal))
            {
                filePath = line["PATH::".Length..];
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                logger.LogDebug("yt-dlp stderr: {Line}", e.Data);
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0 || filePath is null || !File.Exists(filePath))
        {
            throw new InvalidOperationException(
                $"Échec du téléchargement yt-dlp pour '{url}' (code de sortie {process.ExitCode}).");
        }

        logger.LogInformation("yt-dlp: fichier téléchargé -> {Path} (titre : {Title})", filePath, title ?? "(non extrait)");
        return new VideoAcquisitionResult(filePath, title);
    }
}
