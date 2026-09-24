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
}
