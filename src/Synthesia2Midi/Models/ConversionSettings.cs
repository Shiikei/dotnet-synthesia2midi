namespace Synthesia2Midi.Models;

/// <summary>
/// Réglages d'une conversion, sérialisés en JSON dans Conversion.SettingsJson.
/// Reprend les paramètres de la version Python (keyboard_height, threshold,
/// start/end) + les nouvelles options (séparation des mains, détection tempo).
/// </summary>
public class ConversionSettings
{
    /// Fraction 0–1 de la hauteur de l'image où échantillonner la ligne de pixels.
    /// 0.65–0.70 pour les grands claviers, 0.80–0.92 pour les petits (cf. brief).
    public double KeyboardHeight { get; set; } = 0.75;

    /// Écart de luminosité (par rapport à la valeur de repos) au-delà duquel
    /// une touche est considérée enfoncée. Défaut historique : 30.
    public int Threshold { get; set; } = 30;

    public TimeSpan? Start { get; set; }
    public TimeSpan? End { get; set; }

    /// Nouveauté C# : détecter la couleur de la touche enfoncée pour séparer
    /// main gauche / main droite sur 2 canaux MIDI.
    public bool SeparateHandsByColor { get; set; }

    /// Nouveauté C# : estimer le BPM réel depuis les intervalles entre attaques
    /// plutôt que de figer 120 BPM.
    public bool DetectTempo { get; set; } = true;
}
