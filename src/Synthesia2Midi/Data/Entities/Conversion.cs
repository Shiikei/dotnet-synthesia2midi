using Synthesia2Midi.Models;

namespace Synthesia2Midi.Data.Entities;

/// <summary>
/// Une conversion vidéo → MIDI/partition. Les chemins pointent vers des fichiers
/// sur le volume /data — jamais de binaire stocké en base (brief §5).
/// </summary>
public class Conversion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string? SourceUrl { get; set; }
    public string? SourceFileName { get; set; }
    public string Title { get; set; } = string.Empty;

    // DateTime (UTC), pas DateTimeOffset : le provider EF Core Sqlite ne sait pas traduire
    // un ORDER BY sur DateTimeOffset en SQL ("SQLite does not support expressions of type
    // 'DateTimeOffset' in ORDER BY clauses"), utilisé dans History.razor.
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public ConversionStatus Status { get; set; } = ConversionStatus.Pending;
    public string? ErrorMessage { get; set; }

    /// Réglages sérialisés (ConversionSettings) — mémorisés par conversion (brief, historique).
    public string SettingsJson { get; set; } = "{}";

    public string? MidiFilePath { get; set; }
    public string? MusicXmlFilePath { get; set; }
    public string? PdfFilePath { get; set; }
    public string? CalibrationImagePath { get; set; }
}
