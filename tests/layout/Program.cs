// Usage : dotnet run -- <fichier.raw>...   (une ligne RGB24 par fichier, largeur = taille / 3)
// Affiche, pour chaque ligne, le clavier détecté : nombre de touches et étendue (A0..C8 pour 88 touches).
using Microsoft.Extensions.Logging;
using Synthesia2Midi.Services;

using var factory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
var service = new KeyboardAnalysisService(factory.CreateLogger<KeyboardAnalysisService>());
string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
string Name(int midi) => names[midi % 12] + (midi / 12 - 1);

foreach (var path in args.OrderBy(p => p))
{
    var row = File.ReadAllBytes(path);
    try
    {
        var layout = service.DetectLayout(row);
        var low = 60 - layout.MiddleCIndex;
        Console.WriteLine($"{Path.GetFileName(path),-30} {layout.Keys.Count} touches {Name(low)}..{Name(low + layout.Keys.Count - 1)}");
    }
    catch (InvalidOperationException e)
    {
        Console.WriteLine($"{Path.GetFileName(path),-30} ÉCHEC : {e.Message}");
    }
}
