using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using CorrientesIA.Data;
using CorrientesIA.Data.Models;
using CorrientesIA.Scraper.Fetchers;
using CorrientesIA.Scraper.Models;

Console.WriteLine("=== CorrientesIA - Scraper de corpus ===\n");

// ============================================================
// CONFIGURACION
// ============================================================

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile(
        "appsettings.json",
        optional: false,
        reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

var delayMs =
    Math.Max(
        0,
        config.GetValue<int>(
            "ScraperSettings:DelayEntreRequestsMs",
            1500));

var userAgent =
    config["ScraperSettings:UserAgent"]
    ?? "CorrientesIA-Bot/0.1";

var guardarRespaldo =
    config.GetValue<bool>(
        "ScraperSettings:GuardarRespaldoEnDisco",
        true);

var forzarActualizacion =
    config.GetValue<bool>(
        "ScraperSettings:ForzarActualizacion",
        false);

var carpetaRespaldo =
    Path.Combine(
        AppContext.BaseDirectory,
        config["ScraperSettings:CarpetaRespaldo"]
        ?? "../../../data/corpus");

// ============================================================
// CARGAR FUENTES
// ============================================================

var rutaFuentes =
    Path.Combine(
        AppContext.BaseDirectory,
        "fuentes.json");

if (!File.Exists(rutaFuentes))
{
    Console.WriteLine(
        $"ERROR: No existe el archivo de fuentes: {rutaFuentes}");

    return;
}

var fuentesJson =
    await File.ReadAllTextAsync(rutaFuentes);

var fuentes =
    JsonSerializer.Deserialize<List<FuenteConfig>>(
        fuentesJson,
        new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })
    ?? new List<FuenteConfig>();

if (fuentes.Count == 0)
{
    Console.WriteLine(
        "ERROR: No se encontraron fuentes para procesar.");

    return;
}

Console.WriteLine(
    $"{fuentes.Count} fuentes cargadas desde fuentes.json\n");

// ============================================================
// HTTP
// ============================================================

using var http = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(30)
};

http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

var wikipediaFetcher =
    new WikipediaFetcher(http);

var genericoFetcher =
    new GenericHtmlFetcher(http);

// ============================================================
// BASE DE DATOS
// ============================================================

AppDbContext? db = null;

try
{
    var connStr =
        config.GetConnectionString("Default");

    if (string.IsNullOrWhiteSpace(connStr))
    {
        throw new InvalidOperationException(
            "No se encontro ConnectionStrings:Default.");
    }

    var optionsBuilder =
        new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(
                connStr,
                ServerVersion.AutoDetect(connStr));

    db =
        new AppDbContext(
            optionsBuilder.Options);

    if (!await db.Database.CanConnectAsync())
    {
        throw new InvalidOperationException(
            "No se pudo establecer conexion con MySQL.");
    }

    Console.WriteLine(
        "[DB] Conexion MySQL establecida correctamente.");

    // ========================================================
    // DIAGNOSTICO DE CONEXION
    // ========================================================

    var dbConnection =
        db.Database.GetDbConnection();

    Console.WriteLine(
        $"[DB] Base: {dbConnection.Database}");

    Console.WriteLine(
        $"[DB] Servidor: {dbConnection.DataSource}");

    Console.WriteLine(
        "[DB] Tabla objetivo: CorpusDocumentos");

    Console.WriteLine();

    Console.WriteLine(
        "Conectado a MySQL. " +
        "Los documentos se guardaran en CorpusDocumentos.\n");
}
catch (Exception ex)
{
    Console.WriteLine(
        "No se pudo conectar a MySQL:");

    Console.WriteLine(ex.Message);

    Console.WriteLine(
        "Se guardara solo el respaldo en disco.\n");
}

// ============================================================
// RESPALDO EN DISCO
// ============================================================

if (guardarRespaldo)
{
    Directory.CreateDirectory(
        carpetaRespaldo);
}

// ============================================================
// ESTADISTICAS
// ============================================================

int ok = 0;
int fallidos = 0;
int omitidos = 0;

// ============================================================
// PROCESAR FUENTES
// ============================================================

foreach (var fuente in fuentes)
{
    if (string.IsNullOrWhiteSpace(fuente.Url))
    {
        Console.WriteLine(
            "-> Fuente sin URL, omitida.");

        omitidos++;
        continue;
    }

    Console.Write(
        $"-> {fuente.Url} ... ");

    try
    {
        // ----------------------------------------------------
        // BUSCAR DOCUMENTO EXISTENTE
        // ----------------------------------------------------

        CorpusDocumento? documentoExistente = null;

        if (db != null)
        {
            documentoExistente =
                await db.CorpusDocumentos
                    .FirstOrDefaultAsync(
                        d => d.Fuente == fuente.Url);

            if (documentoExistente != null &&
                !forzarActualizacion)
            {
                Console.WriteLine(
                    "ya existe, omitido.");

                omitidos++;
                continue;
            }
        }

        // ----------------------------------------------------
        // SELECCIONAR FETCHER
        // ----------------------------------------------------

        IContentFetcher fetcher =
            fuente.Tipo.Equals(
                "wikipedia",
                StringComparison.OrdinalIgnoreCase)
                ? wikipediaFetcher
                : genericoFetcher;

        // ----------------------------------------------------
        // DESCARGAR CONTENIDO
        // ----------------------------------------------------

        var resultado =
            await fetcher.FetchAsync(
                fuente.Url);

        if (resultado is null ||
            string.IsNullOrWhiteSpace(resultado.Contenido))
        {
            Console.WriteLine(
                "sin contenido util, omitido.");

            omitidos++;
            continue;
        }

        // ----------------------------------------------------
        // GUARDAR / ACTUALIZAR MYSQL
        // ----------------------------------------------------

        if (db != null)
        {
            if (documentoExistente != null)
            {
                documentoExistente.Titulo =
                    resultado.Titulo;

                documentoExistente.Contenido =
                    resultado.Contenido;

                documentoExistente.Procesado =
                    false;
            }
            else
            {
                db.CorpusDocumentos.Add(
                    new CorpusDocumento
                    {
                        Fuente = fuente.Url,
                        Titulo = resultado.Titulo,
                        Contenido = resultado.Contenido,
                        Procesado = false
                    });
            }

            await db.SaveChangesAsync();
        }

        // ----------------------------------------------------
        // GUARDAR RESPALDO UTF-8
        // ----------------------------------------------------

        if (guardarRespaldo)
        {
            var nombreArchivo =
                string.Concat(
                    resultado.Titulo
                        .Split(
                            Path.GetInvalidFileNameChars()))
                + ".txt";

            var rutaArchivo =
                Path.Combine(
                    carpetaRespaldo,
                    nombreArchivo);

            await File.WriteAllTextAsync(
                rutaArchivo,
                resultado.Contenido);
        }

        Console.WriteLine(
            $"ok ({resultado.Contenido.Length} caracteres).");

        ok++;
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine(
            $"ERROR HTTP: {ex.Message}");

        fallidos++;
    }
    catch (TaskCanceledException)
    {
        Console.WriteLine(
            "ERROR: tiempo de espera agotado.");

        fallidos++;
    }
    catch (DbUpdateException ex)
    {
        Console.WriteLine(
            $"ERROR MYSQL: {ex.InnerException?.Message ?? ex.Message}");

        fallidos++;
    }
    catch (Exception ex)
    {
        Console.WriteLine(
            $"ERROR: {ex.Message}");

        fallidos++;
    }

    // --------------------------------------------------------
    // RESPETAR INTERVALO ENTRE SOLICITUDES
    // --------------------------------------------------------

    if (delayMs > 0)
    {
        await Task.Delay(delayMs);
    }
}

// ============================================================
// RESUMEN
// ============================================================

Console.WriteLine(
    "\n============================================================");

Console.WriteLine(
    $"Listo. " +
    $"OK: {ok} | " +
    $"Omitidos: {omitidos} | " +
    $"Fallidos: {fallidos}");

Console.WriteLine(
    "============================================================");

Console.WriteLine(
    $"Respaldo en disco: " +
    (guardarRespaldo
        ? Path.GetFullPath(carpetaRespaldo)
        : "deshabilitado"));