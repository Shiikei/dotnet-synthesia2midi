namespace Synthesia2Midi.Services.Abstractions;

/// Appelle MuseScore en mode headless (mscore4 in.mid -o out.musicxml / out.pdf)
/// pour générer la partition à partir du .mid.
public interface ISheetMusicService
{
    Task<(string MusicXmlPath, string PdfPath)> RenderAsync(
        string midiPath,
        string destinationDirectory,
        CancellationToken cancellationToken);
}
