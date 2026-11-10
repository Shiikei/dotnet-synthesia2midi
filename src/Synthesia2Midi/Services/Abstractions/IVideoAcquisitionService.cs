namespace Synthesia2Midi.Services.Abstractions;

/// Résultat du téléchargement : le chemin local du fichier vidéo, et le titre de la vidéo
/// si yt-dlp a pu l'extraire (utilisé pour afficher un vrai titre dans /historique plutôt
/// que l'URL brute — Title est null en cas d'échec d'extraction du titre, ce qui ne doit
/// jamais faire échouer le téléchargement lui-même).
public record VideoAcquisitionResult(string FilePath, string? Title);

/// Récupère un fichier vidéo local à partir d'une URL (yt-dlp) ; l'upload direct
/// est géré côté page Blazor (écriture disque immédiate) et n'a pas besoin de ce service.
public interface IVideoAcquisitionService
{
    /// Télécharge la vidéo (forçage H.264, cf. brief "Fix important déjà appliqué")
    /// et retourne le chemin du fichier local ainsi que le titre de la vidéo si disponible.
    Task<VideoAcquisitionResult> AcquireFromUrlAsync(string url, string destinationDirectory, CancellationToken cancellationToken);
}
