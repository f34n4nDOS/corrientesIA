using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using CorrientesIA.Data;
using CorrientesIA.Data.Models;
using CorrientesIA.Scraper.Fetchers;
using CorrientesIA.Scraper.Models;

Console.WriteLine("=== CorrientesIA - Scraper de corpus ===\n");

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var delayMs = config.GetValue<int>("ScraperSettings:DelayEntreRequestsMs", 1500);
var userAgent = config["ScraperSettings:UserAgent"] ?? "CorrientesIA-Bot/0.1";
var guardarRespaldo = config.GetValue<bool>("ScraperSettings:GuardarRespaldoEnDisco", true);
var carpetaRespaldo = Path.Combine(AppContext.BaseDirectory, config["ScraperSettings:CarpetaRespaldo"] ?? "../../../data/corpus");

var fuentesJson = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fuentes.json"));
var fuentes = JsonSerializer.Deserialize<List<FuenteConfig>>(fuentesJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
              ?? new List<FuenteConfig>();

Console.WriteLine($"{fuentes.Count} fuentes cargadas desde fuentes.json\n");

using var http = new HttpClient();
http.DefaultRequestHeaders.Add("User-Agent", userAgent);
http.Timeout = TimeSpan.FromSeconds(30);

var wikipediaFetcher = new WikipediaFetcher(http);
var genericoFetcher = new GenericHtmlFetcher(http);

// Intentamos conectar a MySQL; si no esta levantado, seguimos igual
// guardando solo en disco (asi el scraper no depende de tener Docker corriendo).
AppDbContext? db = null;
try
{
    var connStr = config.GetConnectionString("Default");
    var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
        .UseMySql(connStr, ServerVersion.AutoDetect(connStr));
    db = new AppDbContext(optionsBuilder.Options);
    await db.Database.CanConnectAsync();
    Console.WriteLine("Conectado a MySQL. Los documentos se guardaran en CorpusDocumentos.\n");
}
catch
{
    Console.WriteLine("No se pudo conectar a MySQL (¿esta levantado el docker compose?). " +
                       "Se guardara solo el respaldo en disco.\n");
    db = null;
}

if (guardarRespaldo)
    Directory.CreateDirectory(carpetaRespaldo);

int ok = 0, fallidos = 0, omitidos = 0;

foreach (var fuente in fuentes)
{
    Console.Write($"-> {fuente.Url} ... ");

    // Evitar volver a bajar algo que ya esta en la base
    if (db != null && await db.CorpusDocumentos.AnyAsync(d => d.Fuente == fuente.Url))
    {
        Console.WriteLine("ya existe, omitido.");
        omitidos++;
        continue;
    }

    try
    {
        IContentFetcher fetcher = fuente.Tipo == "wikipedia" ? wikipediaFetcher : genericoFetcher;
        var resultado = await fetcher.FetchAsync(fuente.Url);

        if (resultado is null)
        {
            Console.WriteLine("sin contenido util, omitido.");
            omitidos++;
        }
        else
        {
            if (db != null)
            {
                db.CorpusDocumentos.Add(new CorpusDocumento
                {
                    Fuente = fuente.Url,
                    Titulo = resultado.Titulo,
                    Contenido = resultado.Contenido,
                    Procesado = false
                });
                await db.SaveChangesAsync();
            }

            if (guardarRespaldo)
            {
                var nombreArchivo = string.Concat(resultado.Titulo.Split(Path.GetInvalidFileNameChars())) + ".txt";
                await File.WriteAllTextAsync(Path.Combine(carpetaRespaldo, nombreArchivo), resultado.Contenido);
            }

            Console.WriteLine($"ok ({resultado.Contenido.Length} caracteres).");
            ok++;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: {ex.Message}");
        fallidos++;
    }

    await Task.Delay(delayMs); // ser buen ciudadano con los servidores que scrapeamos
}

Console.WriteLine($"\nListo. OK: {ok} | Omitidos: {omitidos} | Fallidos: {fallidos}");
Console.WriteLine($"Respaldo en disco: {(guardarRespaldo ? Path.GetFullPath(carpetaRespaldo) : "deshabilitado")}");
