using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using CorrientesIA.Scraper.Models;

namespace CorrientesIA.Scraper.Fetchers;

// Fetcher especifico para articulos de Wikipedia en espanol.
// Extrae solo los parrafos del cuerpo del articulo (#mw-content-text),
// descarta tablas, referencias, notas al pie, cajas laterales, etc.
public class WikipediaFetcher : IContentFetcher
{
    private readonly HttpClient _http;

    public WikipediaFetcher(HttpClient http) => _http = http;

    public async Task<ResultadoScrapeo?> FetchAsync(string url)
    {
        var html = await _http.GetStringAsync(url);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var titulo = doc.DocumentNode
            .SelectSingleNode("//h1[@id='firstHeading']")
            ?.InnerText?.Trim() ?? "(sin titulo)";

        var contenedor = doc.DocumentNode.SelectSingleNode("//div[@id='mw-content-text']");
        if (contenedor == null) return null;

        // Quitamos elementos que no aportan texto util para entrenar:
        // tablas (infoboxes, tablas de datos), referencias, notas, cajas de navegacion.
        var nodosAEliminar = contenedor.SelectNodes(
            ".//table | .//sup[contains(@class,'reference')] | .//div[contains(@class,'navbox')] " +
            "| .//div[contains(@class,'reflist')] | .//ol[contains(@class,'references')] " +
            "| .//div[contains(@class,'infobox')] | .//style | .//script");

        if (nodosAEliminar != null)
            foreach (var nodo in nodosAEliminar)
                nodo.Remove();

        var parrafos = contenedor.SelectNodes(".//p");
        if (parrafos == null) return null;

        var sb = new StringBuilder();
        foreach (var p in parrafos)
        {
            var texto = HtmlEntity.DeEntitize(p.InnerText).Trim();
            texto = Regex.Replace(texto, @"\[\d+\]", "");        // quitar marcas de referencia [1], [2]...
            texto = Regex.Replace(texto, @"\s+", " ").Trim();     // colapsar espacios

            if (texto.Length > 30) // descartamos parrafos vacios/ruido
                sb.AppendLine(texto);
        }

        var contenido = sb.ToString().Trim();
        return contenido.Length < 100 ? null : new ResultadoScrapeo(titulo, contenido);
    }
}
