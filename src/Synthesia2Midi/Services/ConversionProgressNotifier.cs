using Synthesia2Midi.Models;

namespace Synthesia2Midi.Services;

/// <summary>
/// Diffusion EN MÉMOIRE de la progression des conversions vers les pages Blazor Server.
/// Les pages tournent dans le même process que le worker : elles s'abonnent ici au lieu
/// d'ouvrir une connexion SignalR cliente vers l'URL publique du site. Ce détour réseau
/// échouait dès que le conteneur ne pouvait pas joindre sa propre adresse publique
/// (accès externe via syn2mid.shikei.be : « Connection refused », circuit Blazor planté,
/// partition jamais chargée — bug observé le 2026-11-09).
/// </summary>
public class ConversionProgressNotifier(ILogger<ConversionProgressNotifier> logger)
{
    public event Func<ConversionProgress, Task>? ProgressUpdated;

    public async Task PublishAsync(ConversionProgress progress)
    {
        var handlers = ProgressUpdated;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Func<ConversionProgress, Task>>())
        {
            try
            {
                await handler(progress);
            }
            catch (Exception ex)
            {
                // Une page fermée ou en erreur ne doit jamais bloquer le worker.
                logger.LogDebug(ex, "Abonné à la progression en erreur, ignoré.");
            }
        }
    }
}
