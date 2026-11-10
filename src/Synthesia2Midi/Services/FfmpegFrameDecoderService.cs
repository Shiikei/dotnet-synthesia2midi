using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Synthesia2Midi.Services.Abstractions;

namespace Synthesia2Midi.Services;

/// <summary>
/// Décodage via ffmpeg en pipe rawvideo (brief §"Cible" point 2), sans OpenCV.
/// Optimisation clé : un filtre ffmpeg `crop` réduit chaque frame à 1 pixel de haut
/// AVANT l'encodage rawvideo — on ne fait donc transiter que largeur×3 octets par
/// frame sur le pipe, jamais l'image complète.
/// </summary>
public class FfmpegFrameDecoderService(ILogger<FfmpegFrameDecoderService> logger) : IFrameDecoderService
{
    public async IAsyncEnumerable<PixelRow> ReadKeyboardRowsAsync(
        string videoPath,
        double keyboardHeightFraction,
        TimeSpan? start,
        TimeSpan? end,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (width, height, fps) = await ProbeAsync(videoPath, cancellationToken);
        var y = Math.Clamp((int)Math.Round(height * keyboardHeightFraction), 0, height - 1);

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (start is { } s)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(FormatTimeSpan(s));
        }

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);

        if (end is { } e)
        {
            var duration = start is { } s2 ? e - s2 : e;
            if (duration > TimeSpan.Zero)
            {
                psi.ArgumentList.Add("-t");
                psi.ArgumentList.Add(FormatTimeSpan(duration));
            }
        }

        // crop=...:exact=1 → ne garde que la ligne à keyboard_height. exact=1 est indispensable :
        // sans lui, ffmpeg arrondit largeur/hauteur au multiple pair le plus proche pour les vidéos
        // en 4:2:0 (yuv420p, très courant) — notre hauteur de 1px serait arrondie à 0 et planterait
        // ("Invalid too big or non positive size for ... height '0'"). -an/-sn : pas d'audio/
        // sous-titres à décoder, inutile pour notre usage.
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add($"crop=w=iw:h=1:x=0:y={y}:exact=1");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("rgb24");
        psi.ArgumentList.Add("-an");
        psi.ArgumentList.Add("-sn");
        psi.ArgumentList.Add("-");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Impossible de démarrer ffmpeg.");

        // Vide stderr en tâche de fond pour ne jamais bloquer sur un pipe plein.
        var stderrTask = DrainStderrAsync(process, cancellationToken);

        var bytesPerRow = width * 3;
        var stdout = process.StandardOutput.BaseStream;
        var frameIndex = 0;

        // Le timestamp est RELATIF à `start` (donc à la première frame décodée), pas
        // au temps absolu de la vidéo source. ffmpeg (-ss) a déjà positionné le flux
        // décodé à `start` : frameIndex / fps est donc l'écart depuis ce point. Ce
        // timestamp relatif est ensuite utilisé tel quel pour écrire les ticks MIDI
        // (DryWetMidiWriterService.SecondsToTicks) — le clip .mid démarre donc à 0,
        // sans silence initial correspondant à `start`, quelle que soit sa valeur.
        while (true)
        {
            var buffer = new byte[bytesPerRow];
            var complete = await ReadExactAsync(stdout, buffer, cancellationToken);
            if (!complete)
            {
                break;
            }

            var timestamp = frameIndex / fps;
            yield return new PixelRow(frameIndex, timestamp, buffer);
            frameIndex++;
        }

        await process.WaitForExitAsync(cancellationToken);
        var stderrOutput = await stderrTask;

        if (process.ExitCode != 0)
        {
            if (frameIndex == 0)
            {
                // Zéro frame décodée : on lève directement, avec la commande et la sortie
                // ffmpeg complètes dans le message — remonte jusqu'à Conversion.ErrorMessage,
                // visible sur la page de détail, sans avoir besoin de fouiller les logs Docker.
                var command = "ffmpeg " + string.Join(' ', psi.ArgumentList);
                throw new InvalidOperationException(
                    $"ffmpeg a échoué (code {process.ExitCode}) sans produire aucune frame.\n" +
                    $"Commande : {command}\n" +
                    $"Sortie ffmpeg :\n{stderrOutput}");
            }

            logger.LogWarning(
                "ffmpeg s'est terminé avec le code {Code} après {Frames} frame(s) décodée(s) pour {Path}. Sortie ffmpeg : {Stderr}",
                process.ExitCode, frameIndex, videoPath, stderrOutput);
        }
    }

    private async Task<string> DrainStderrAsync(Process process, CancellationToken cancellationToken)
    {
        var buffer = new System.Text.StringBuilder();
        string? line;
        while ((line = await process.StandardError.ReadLineAsync(cancellationToken)) is not null)
        {
            logger.LogDebug("ffmpeg: {Line}", line);
            buffer.AppendLine(line);
        }
        return buffer.ToString();
    }

    /// Lit exactement buffer.Length octets, ou renvoie false si le flux se termine
    /// proprement AVANT le premier octet (fin normale). Une coupure EN COURS de frame
    /// lève une exception (flux tronqué, probablement un plantage ffmpeg).
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                if (offset == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("Flux ffmpeg tronqué en cours de frame (plantage probable).");
            }

            offset += read;
        }

        return true;
    }

    private async Task<(int Width, int Height, double Fps)> ProbeAsync(string videoPath, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffprobe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams");
        psi.ArgumentList.Add("v:0");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=width,height,avg_frame_rate,r_frame_rate");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add(videoPath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Impossible de démarrer ffprobe.");

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe a échoué (code {process.ExitCode}) pour '{videoPath}'.");
        }

        using var doc = JsonDocument.Parse(stdout);
        var streams = doc.RootElement.GetProperty("streams");
        if (streams.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"Aucun flux vidéo détecté dans '{videoPath}'.");
        }

        var stream = streams[0];
        var width = stream.GetProperty("width").GetInt32();
        var height = stream.GetProperty("height").GetInt32();

        var avgFrameRate = stream.TryGetProperty("avg_frame_rate", out var afr) ? afr.GetString() : null;
        var rFrameRate = stream.TryGetProperty("r_frame_rate", out var rfr) ? rfr.GetString() : null;
        var fpsRaw = (avgFrameRate is not null && avgFrameRate != "0/0") ? avgFrameRate : rFrameRate;
        var fps = ParseFrameRate(fpsRaw ?? "30/1");

        return (width, height, fps);
    }

    private static double ParseFrameRate(string value)
    {
        var parts = value.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var num)
            && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var den)
            && den != 0)
        {
            return num / den;
        }

        return double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var direct) ? direct : 30.0;
    }

    private static string FormatTimeSpan(TimeSpan value) => value.ToString(@"hh\:mm\:ss\.fff");
}
