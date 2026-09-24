namespace CorrientesIA.Data.Models;

// Texto crudo recolectado por el scraper, antes de tokenizar/entrenar.
public class CorpusDocumento
{
    public int Id { get; set; }
    public string Fuente { get; set; } = string.Empty;   // URL o nombre de fuente
    public string Titulo { get; set; } = string.Empty;
    public string Contenido { get; set; } = string.Empty;
    public bool Procesado { get; set; } = false;
    public DateTime FechaScrapeo { get; set; } = DateTime.UtcNow;
}
