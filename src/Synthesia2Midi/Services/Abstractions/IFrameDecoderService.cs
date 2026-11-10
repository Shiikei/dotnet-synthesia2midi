namespace Synthesia2Midi.Services.Abstractions;

/// Décode une vidéo via ffmpeg (pipe rawvideo, sans OpenCV — brief §"Cible" point 2)
/// et expose, frame par frame, la ligne de pixels correspondant à keyboard_height.
public interface IFrameDecoderService
{
    IAsyncEnumerable<PixelRow> ReadKeyboardRowsAsync(
        string videoPath,
        double keyboardHeightFraction,
        TimeSpan? start,
        TimeSpan? end,
        CancellationToken cancellationToken);
}

/// Une ligne de pixels RGB (largeur variable) pour une frame donnée.
public readonly record struct PixelRow(int FrameIndex, double TimestampSeconds, ReadOnlyMemory<byte> Rgb);
