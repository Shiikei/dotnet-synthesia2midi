namespace Synthesia2Midi.Services.Abstractions;

/// Écrit un fichier .mid (DryWetMIDI) à partir des événements détectés.
public interface IMidiWriterService
{
    /// bpm : tempo détecté (brief §"Détection du tempo") ou 120 par défaut.
    /// Rappel timing version Python : 480 ticks/noire à 120 BPM,
    /// ticks_par_frame = 960/fps — à généraliser pour un BPM variable.
    Task<string> WriteAsync(
        IEnumerable<NoteEvent> noteEvents,
        int bpm,
        string destinationPath,
        CancellationToken cancellationToken);
}
