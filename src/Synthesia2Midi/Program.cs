using Microsoft.EntityFrameworkCore;
using Synthesia2Midi.Components;
using Synthesia2Midi.Data;
using Synthesia2Midi.Hubs;
using Synthesia2Midi.Services;
using Synthesia2Midi.Services.Abstractions;

var builder = WebApplication.CreateBuilder(args);

// TLS piloté par variables d'environnement (brief §7) — voir TlsConfigurator.
TlsConfigurator.Configure(builder);

// --- Blazor Server + SignalR ---
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSignalR();

// --- Persistance : SQLite via EF Core, fichier dans /data ---
var dataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? "/data";
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "synthesia2midi.db");
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));

// --- Pipeline de conversion (interfaces + implémentations, cf. Services/Abstractions) ---
builder.Services.AddScoped<IVideoAcquisitionService, YtDlpVideoAcquisitionService>();
builder.Services.AddScoped<IFrameDecoderService, FfmpegFrameDecoderService>();
builder.Services.AddScoped<IKeyboardAnalysisService, KeyboardAnalysisService>();
builder.Services.AddScoped<IMidiWriterService, DryWetMidiWriterService>();
builder.Services.AddScoped<ISheetMusicService, MuseScoreSheetMusicService>();

// --- Test des réglages sur un extrait (page /nouvelle) ---
builder.Services.AddSingleton<ThresholdPreviewService>();

// --- File de conversion (Channel<T>) + worker unique ---
builder.Services.AddSingleton<ConversionQueue>();
builder.Services.AddSingleton<ConversionProgressNotifier>();
builder.Services.AddHostedService<ConversionBackgroundService>();

var app = builder.Build();

// Création du schéma au démarrage. EnsureCreated() suffit pour ce squelette
// (pas de migrations EF Core encore générées). À remplacer par
// db.Database.Migrate() une fois `dotnet ef migrations add InitialCreate` lancé,
// si une évolution de schéma versionnée devient utile.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHub<ConversionProgressHub>("/hubs/conversion-progress");

// Téléchargement des livrables d'une conversion (MIDI / MusicXML / PDF / calibration).
app.MapGet("/api/conversions/{id:guid}/{kind}", async (Guid id, string kind, AppDbContext db) =>
{
    var conversion = await db.Conversions.FindAsync(id);
    if (conversion is null)
    {
        return Results.NotFound();
    }

    var path = kind switch
    {
        "midi" => conversion.MidiFilePath,
        "musicxml" => conversion.MusicXmlFilePath,
        "pdf" => conversion.PdfFilePath,
        "calibration" => conversion.CalibrationImagePath,
        _ => null,
    };

    if (path is null || !File.Exists(path))
    {
        return Results.NotFound();
    }

    var contentType = kind switch
    {
        "midi" => "audio/midi",
        "musicxml" => "application/vnd.recordare.musicxml+xml",
        "pdf" => "application/pdf",
        "calibration" => "image/png",
        _ => "application/octet-stream",
    };

    return Results.File(path, contentType, Path.GetFileName(path));
});

// Image de l'instant « Début » utilisée par l'aperçu de /nouvelle.
app.MapGet("/api/preview/{key}/frame.jpg", (string key, ThresholdPreviewService preview) =>
{
    var path = preview.GetFramePath(key);
    return path is null ? Results.NotFound() : Results.File(path, "image/jpeg");
});

app.Run();
