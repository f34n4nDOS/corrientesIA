namespace CorrientesIA.Data.Models;

// Tabla generica clave-valor para cifras concretas (poblacion, fechas, indices INDEC, etc.)
// que el modelo generativo no debe "memorizar" sino consultar en el momento.
public class DatoDuro
{
    public int Id { get; set; }
    public string Clave { get; set; } = string.Empty;      // ej: "poblacion_capital_2022"
    public string Valor { get; set; } = string.Empty;      // ej: "358223"
    public string Fuente { get; set; } = string.Empty;      // ej: "INDEC Censo 2022"
    public DateTime FechaActualizacion { get; set; } = DateTime.UtcNow;
}
