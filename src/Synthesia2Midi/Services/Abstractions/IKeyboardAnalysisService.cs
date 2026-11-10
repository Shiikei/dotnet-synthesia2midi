namespace Synthesia2Midi.Services.Abstractions;

/// Détection des touches par seuillage adaptatif, repérage du do central,
/// et extraction des événements note_on/note_off à partir des lignes de pixels.
/// Reprend l'algorithme de la version Python (brief §"Algorithme de conversion").
public interface IKeyboardAnalysisService
{
    /// Seuillage adaptatif sur la 1ère frame : seuil blanc = min + 60% amplitude,
    /// seuil noir = min + 40%, largeur mini 3px. Touches triées par x = ordre chromatique.
    KeyboardLayout DetectLayout(ReadOnlySpan<byte> firstFrameRow);

    /// Motif [W,W,B,W,B,W,W] → l'index i+1 est un do ; prendre le do médian.
    /// |luminosité − repos| > threshold ⇒ touche enfoncée. Si separateHandsByColor,
    /// la couleur de la touche enfoncée détermine le canal (main gauche/droite).
    IEnumerable<NoteEvent> ExtractNoteEvents(
        KeyboardLayout layout,
        IEnumerable<PixelRow> rows,
        int threshold,
        bool separateHandsByColor);

    /// Heuristique grossière basée sur les intervalles entre attaques (brief §"Détection du
    /// tempo") — voir le commentaire d'implémentation pour ses limites connues.
    int EstimateBpm(IReadOnlyList<NoteEvent> noteEvents);
}

public record KeyboardLayout(IReadOnlyList<KeyRegion> Keys, int MiddleCIndex);

public record KeyRegion(int Index, int StartX, int EndX, bool IsWhite);

/// Channel : 0 = main droite / indifférencié, 1 = main gauche (si separateHandsByColor).
public record NoteEvent(int MidiNote, double TimestampSeconds, bool IsNoteOn, int Velocity, int Channel);
