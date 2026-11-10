using System.Security.Cryptography.X509Certificates;

namespace Synthesia2Midi.Services;

/// <summary>
/// Configure Kestrel selon les variables d'environnement TLS_* (brief §7).
/// - TLS_MODE=auto|pfx|pem|none (auto = détection selon les fichiers présents)
/// - Un mode forcé avec fichier manquant ou mot de passe invalide fait échouer
///   le démarrage explicitement : jamais de repli silencieux en HTTP.
/// </summary>
public static class TlsConfigurator
{
    public static void Configure(WebApplicationBuilder builder)
    {
        var mode = (Environment.GetEnvironmentVariable("TLS_MODE") ?? "auto").ToLowerInvariant();
        var pfxPath = Environment.GetEnvironmentVariable("TLS_PFX_PATH") ?? "/certs/cert.pfx";
        var pfxPassword = Environment.GetEnvironmentVariable("TLS_PFX_PASSWORD");
        var certPath = Environment.GetEnvironmentVariable("TLS_CERT_PATH") ?? "/certs/cert.pem";
        var keyPath = Environment.GetEnvironmentVariable("TLS_KEY_PATH") ?? "/certs/key.pem";

        var resolvedMode = mode;
        if (mode == "auto")
        {
            if (File.Exists(pfxPath))
            {
                resolvedMode = "pfx";
            }
            else if (File.Exists(certPath) && File.Exists(keyPath))
            {
                resolvedMode = "pem";
            }
            else
            {
                resolvedMode = "none";
            }
        }

        builder.WebHost.ConfigureKestrel(options =>
        {
            switch (resolvedMode)
            {
                case "pfx":
                    ConfigurePfx(options, pfxPath, pfxPassword);
                    break;

                case "pem":
                    ConfigurePem(options, certPath, keyPath);
                    break;

                case "none":
                    options.ListenAnyIP(8080);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"TLS_MODE inconnu : '{mode}'. Valeurs acceptées : auto, pfx, pem, none.");
            }
        });
    }

    private static void ConfigurePfx(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions options, string pfxPath, string? pfxPassword)
    {
        if (!File.Exists(pfxPath))
        {
            throw new InvalidOperationException(
                $"TLS_MODE=pfx mais le fichier est introuvable : {pfxPath}. " +
                "Vérifie TLS_PFX_PATH et le montage du volume ./certs.");
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, pfxPassword);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Impossible de charger le certificat PFX ({pfxPath}) : mot de passe invalide ou fichier corrompu.", ex);
        }

        options.ListenAnyIP(8443, listenOptions => listenOptions.UseHttps(certificate));
    }

    private static void ConfigurePem(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions options, string certPath, string keyPath)
    {
        if (!File.Exists(certPath) || !File.Exists(keyPath))
        {
            throw new InvalidOperationException(
                $"TLS_MODE=pem mais les fichiers sont introuvables : {certPath} / {keyPath}. " +
                "Vérifie TLS_CERT_PATH, TLS_KEY_PATH et le montage du volume ./certs.");
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Impossible de charger le certificat PEM ({certPath} / {keyPath}).", ex);
        }

        options.ListenAnyIP(8443, listenOptions => listenOptions.UseHttps(certificate));
    }
}
