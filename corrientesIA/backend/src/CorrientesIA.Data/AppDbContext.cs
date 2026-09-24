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
    }
}
