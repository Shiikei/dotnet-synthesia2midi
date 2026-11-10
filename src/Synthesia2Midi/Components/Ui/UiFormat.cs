using System.Globalization;
using Synthesia2Midi.Models;

namespace Synthesia2Midi.Components.Ui;

/// <summary>
/// Mise en forme des valeurs affichées, selon les règles de rédaction du design system
/// syn2mid : temps en mm:ss, dates « 8 nov. 2026, 19:10 », espace insécable avant « % ».
/// Écrit sans dépendre des données de culture (le conteneur peut tourner en mode invariant).
/// </summary>
public static class UiFormat
{
    private const char Nbsp = ' ';

    private static readonly string[] Months =
        { "janv.", "févr.", "mars", "avr.", "mai", "juin", "juil.", "août", "sept.", "oct.", "nov.", "déc." };

    private static readonly Lazy<TimeZoneInfo> LocalZone = new(() =>
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Brussels"); }
        catch { return TimeZoneInfo.Utc; }
    });

    /// mm:ss, ou h:mm:ss au-delà d'une heure.
    public static string Time(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0)
        {
            seconds = 0;
        }
        var t = TimeSpan.FromSeconds(Math.Floor(seconds));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes + (t.Hours * 60):00}:{t.Seconds:00}";
    }

    /// Lit « 75 », « 1:15 », « 01:15 » ou « 1:02:03 ». Vide = valide, sans valeur.
    public static bool TryParseTime(string? text, out double? seconds)
    {
        seconds = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length > 3)
        {
            return false;
        }

        double total = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) || v < 0)
            {
                return false;
            }
            // Les minutes et secondes après le premier champ restent sous 60.
            if (i > 0 && v >= 60)
            {
                return false;
            }
            total = (total * 60) + v;
        }

        seconds = total;
        return true;
    }

    public static string TimeInput(TimeSpan? value) => value is TimeSpan t ? Time(t.TotalSeconds) : string.Empty;

    /// « 8 nov. 2026, 19:10 », à l'heure de Bruxelles.
    public static string Date(DateTime utc)
    {
        var u = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(u, LocalZone.Value);
        return $"{local.Day} {Months[local.Month - 1]} {local.Year}, {local.Hour:00}:{local.Minute:00}";
    }

    public static string IsoDate(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// « 75 % » (espace insécable).
    public static string Percent(double value) => $"{Math.Round(value).ToString(CultureInfo.InvariantCulture)}{Nbsp}%";

    /// « 820 o », « 12 Ko », « 1,4 Mo ».
    public static string FileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes}{Nbsp}o";
        }
        if (bytes < 1024 * 1024)
        {
            return $"{Math.Max(1, Math.Round(bytes / 1024.0)).ToString(CultureInfo.InvariantCulture)}{Nbsp}Ko";
        }
        var mo = Math.Round(bytes / (1024.0 * 1024.0), 1).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');
        return $"{mo}{Nbsp}Mo";
    }

    /// Nombre pour un attribut HTML (point décimal, quelle que soit la culture).
    public static string Inv(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    public static string StatusKind(ConversionStatus status) => status switch
    {
        ConversionStatus.Pending => "queued",
        ConversionStatus.Completed => "done",
        ConversionStatus.Failed => "failed",
        _ => "running",
    };

    public static string StatusLabel(ConversionStatus status) => status switch
    {
        ConversionStatus.Pending => "En attente",
        ConversionStatus.Completed => "Terminée",
        ConversionStatus.Failed => "Échec",
        _ => "En cours",
    };

    public static string StatusIcon(ConversionStatus status) => status switch
    {
        ConversionStatus.Pending => "clock",
        ConversionStatus.Completed => "check",
        ConversionStatus.Failed => "alert",
        _ => "spinner",
    };

    /// Nom de fichier sûr à partir d'un titre de vidéo.
    public static string SafeFileName(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "conversion";
        }
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '"', '<', '>', '|', ':', '*', '?', '\\', '/' }).ToHashSet();
        var cleaned = new string(title.Select(c => invalid.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray());
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '.');
        if (cleaned.Length > 80)
        {
            cleaned = cleaned[..80].TrimEnd(' ', '.');
        }
        return cleaned.Length == 0 ? "conversion" : cleaned;
    }
}
