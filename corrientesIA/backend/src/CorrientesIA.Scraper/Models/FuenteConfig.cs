namespace CorrientesIA.Scraper.Models;

// Una entrada de fuentes.json: de donde sacamos texto y con que fetcher.
public class FuenteConfig
{
    public string Url { get; set; } = string.Empty;
    public string Categoria { get; set; } = string.Empty; // geografia, historia, turismo, cultura, tramite...
    public string Tipo { get; set; } = "generico";        // "wikipedia" | "generico"
}

public record ResultadoScrapeo(string Titulo, string Contenido);
