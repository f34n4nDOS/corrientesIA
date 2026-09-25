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

        if (palabras.Any(p => clavePalabras.Contains(p)))
        {
            return $"{dato.Clave.Replace('_', ' ')}: {dato.Valor} (fuente: {dato.Fuente})";
        }
    }

    return null;
}
}
