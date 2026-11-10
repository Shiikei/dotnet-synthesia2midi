namespace Synthesia2Midi.Models;

/// DTO poussé via SignalR (groupe = ConversionId.ToString()) pour la progression temps réel.
public record ConversionProgress(Guid ConversionId, ConversionStatus Status, double PercentComplete, string? Message);
