using Microsoft.EntityFrameworkCore;
using CorrientesIA.Data.Models;

namespace CorrientesIA.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Lugar> Lugares => Set<Lugar>();
    public DbSet<DatoDuro> DatosDuros => Set<DatoDuro>();
    public DbSet<CorpusDocumento> CorpusDocumentos => Set<CorpusDocumento>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DatoDuro>().HasIndex(d => d.Clave).IsUnique();

        modelBuilder.Entity<Lugar>().HasData(
            new Lugar
            {
                Id = 1,
                Nombre = "Costanera General San Martin",
                Categoria = "turismo",
                Descripcion = "Costanera de la ciudad de Corrientes.",
                Localidad = "Corrientes Capital",
                Latitud = null,
                Longitud = null,
                FechaActualizacion = new DateTime(2026, 1, 1)
            },
            new Lugar
            {
                Id = 2,
                Nombre = "Esteros del Ibera",
                Categoria = "turismo",
                Descripcion = "Gran humedal ubicado en la provincia de Corrientes.",
                Localidad = "Provincia de Corrientes",
                Latitud = null,
                Longitud = null,
                FechaActualizacion = new DateTime(2026, 1, 1)
            }
        );
    }
}