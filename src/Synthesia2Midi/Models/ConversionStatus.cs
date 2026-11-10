namespace Synthesia2Midi.Models;

public enum ConversionStatus
{
    Pending,
    Downloading,
    Analyzing,
    WritingMidi,
    RenderingSheetMusic,
    Completed,
    Failed,
}
