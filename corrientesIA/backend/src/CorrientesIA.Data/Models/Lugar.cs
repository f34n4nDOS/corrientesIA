namespace CorrientesIA.Data.Models;

// Datos "duros" de la provincia (lugares turisticos, historicos, tramites, etc.)
// Esta es la capa de "grounding": lo que el transformer NO debe inventar,
// se consulta aca y se inyecta en la respuesta final.
public class Lugar
{
    public int Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string Categoria { get; set; } = string.Empty; // turismo, historia, tramite, cultura...
    public string Descripcion { get; set; } = string.Empty;
    public string? Localidad { get; set; }
    public double? Latitud { get; set; }
    public double? Longitud { get; set; }
    public DateTime FechaActualizacion { get; set; } = DateTime.UtcNow;
}
