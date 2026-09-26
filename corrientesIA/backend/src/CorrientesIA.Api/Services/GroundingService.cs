using CorrientesIA.Data;
using Microsoft.EntityFrameworkCore;

namespace CorrientesIA.Api.Services;

// Capa de "grounding": antes o despues de generar texto, busca datos duros
// en MySQL para que fechas/cifras/lugares no dependan de lo que el
// transformer alucine.
public class GroundingService
{
    private readonly AppDbContext _db;

    public GroundingService(AppDbContext db) => _db = db;

    public async Task<string?> BuscarDatoDuroAsync(string clave)
    {
        var dato = await _db.DatosDuros.FirstOrDefaultAsync(d => d.Clave == clave);
        return dato?.Valor;
    }

    public async Task<List<string>> BuscarLugaresAsync(string categoria)
    {
        return await _db.Lugares
            .Where(l => l.Categoria == categoria)
            .Select(l => l.Nombre)
            .ToListAsync();
    }

    /// <summary>
    /// Busqueda simple por palabras clave: si el mensaje del usuario menciona
    /// algo que coincide con un Lugar o un DatoDuro, devolvemos ese dato
    /// verificado para agregarlo a la respuesta del modelo (asi las cifras
    /// concretas no dependen de lo que el transformer haya "memorizado").
    /// Fase 1: coincidencia por substring. Mas adelante se puede mejorar
    /// con un clasificador de intencion entrenado aparte.
    /// </summary>
    public async Task<string?> BuscarPorPalabrasClaveAsync(string mensaje)
{
    var palabrasGenericas = new HashSet<string>
{
    "ciudad",
    "provincia",
    "lugares",
    "lugar",
    "turisticos",
    "turistico",
    "habitantes",
    "fecha",
    "cuando",
    "donde",
    "cual",
    "cuales",
    "que"
};

var palabras = mensaje
    .ToLowerInvariant()
    .Split(
        new[] { ' ', ',', '.', '?', '!', ';', ':', '¿', '¡' },
        StringSplitOptions.RemoveEmptyEntries
    )
    .Where(p => p.Length >= 4)
    .Where(p => !palabrasGenericas.Contains(p))
    .ToHashSet();

    if (palabras.Count == 0)
        return null;

    var lugares = await _db.Lugares.ToListAsync();

    foreach (var lugar in lugares)
    {
        var nombrePalabras = lugar.Nombre
            .ToLowerInvariant()
            .Split(
                new[] { ' ', ',', '.', '?', '!', ';', ':', '¿', '¡' },
                StringSplitOptions.RemoveEmptyEntries
            )
            .ToHashSet();

        if (palabras.Any(p => nombrePalabras.Contains(p)))
        {
            return $"{lugar.Nombre} — {lugar.Descripcion}";
        }
    }

    var datos = await _db.DatosDuros.ToListAsync();

    foreach (var dato in datos)
    {
        var clavePalabras = dato.Clave
            .ToLowerInvariant()
            .Replace('_', ' ')
            .Split(
                new[] { ' ', ',', '.', '?', '!', ';', ':', '¿', '¡' },
                StringSplitOptions.RemoveEmptyEntries
            )
            .ToHashSet();

        if (palabras.Count(p => clavePalabras.Contains(p)) >= 2)
        {
            return $"{dato.Clave.Replace('_', ' ')}: {dato.Valor} (fuente: {dato.Fuente})";
        }
    }

    return null;
}
public async Task<string?> BuscarEnCorpusAsync(string mensaje)
{
    var palabras = mensaje
        .ToLowerInvariant()
        .Split(
            new[] { ' ', ',', '.', '?', '!', ';', ':', '¿', '¡' },
            StringSplitOptions.RemoveEmptyEntries
        )
        .Where(p => p.Length >= 4)
        .ToHashSet();

    if (palabras.Count == 0)
        return null;

    var documentos = await _db.CorpusDocumentos.ToListAsync();
    var consultaSegundaCiudad =
    palabras.Contains("segunda") &&
    (palabras.Contains("poblada") || palabras.Contains("ciudad"));

if (consultaSegundaCiudad)
{
    var fraseClave = "segunda ciudad más poblada";

    var documentoFrase = documentos.FirstOrDefault(d =>
        d.Contenido.Contains(fraseClave, StringComparison.OrdinalIgnoreCase));

    if (documentoFrase is not null)
    {
        var posicionFrase = documentoFrase.Contenido.IndexOf(
            fraseClave,
            StringComparison.OrdinalIgnoreCase
        );

        var inicioFrase = Math.Max(0, posicionFrase - 100);
        var longitudFrase = Math.Min(
            700,
            documentoFrase.Contenido.Length - inicioFrase
        );

        var contextoFrase = documentoFrase.Contenido
            .Substring(inicioFrase, longitudFrase)
            .Trim();

        return $"{documentoFrase.Titulo}: ...{contextoFrase}...";
    }
}
    var resultados = documentos
        .Select(documento =>
        {
            var texto = $"{documento.Titulo} {documento.Contenido}"
                .ToLowerInvariant();

            var coincidencias = palabras.Count(p => texto.Contains(p));

var coincidenciasTitulo = palabras.Count(p =>
    documento.Titulo.Contains(p, StringComparison.OrdinalIgnoreCase));

var puntuacion = coincidencias + (coincidenciasTitulo * 10);

// Si varias palabras importantes aparecen juntas en el comienzo
// del documento, damos prioridad a ese documento.
var inicioTexto = documento.Contenido.Length > 1000
    ? documento.Contenido[..1000]
    : documento.Contenido;

var coincidenciasInicio = palabras.Count(p =>
    inicioTexto.Contains(p, StringComparison.OrdinalIgnoreCase));

puntuacion += coincidenciasInicio * 5;

            return new
            {
                Documento = documento,
                Puntuacion = puntuacion
            };
        })
        .Where(x => x.Puntuacion > 0)
        .OrderByDescending(x => x.Puntuacion)
        .ToList();

    var mejorResultado = resultados.FirstOrDefault();

    if (mejorResultado is null)
        return null;

    var documentoEncontrado = mejorResultado.Documento;

    var posicion = documentoEncontrado.Contenido.IndexOf(
        palabras.FirstOrDefault(p =>
            documentoEncontrado.Contenido.Contains(
                p,
                StringComparison.OrdinalIgnoreCase)) ?? "",
        StringComparison.OrdinalIgnoreCase
    );

    if (posicion < 0)
        posicion = 0;

    var inicio = Math.Max(0, posicion - 200);
    var longitud = Math.Min(700, documentoEncontrado.Contenido.Length - inicio);

    var contexto = documentoEncontrado.Contenido
        .Substring(inicio, longitud)
        .Trim();

    return $"{documentoEncontrado.Titulo}: ...{contexto}...";
}
}
