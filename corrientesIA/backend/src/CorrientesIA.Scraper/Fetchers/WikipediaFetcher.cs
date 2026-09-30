using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using CorrientesIA.Scraper.Models;

namespace CorrientesIA.Scraper.Fetchers;

// Fetcher específico para artículos de Wikipedia en español.
// Extrae solo los párrafos del cuerpo del artículo (#mw-content-text),
// descarta tablas, referencias, notas al pie, cajas laterales, etc.
public class WikipediaFetcher : IContentFetcher
{
    private readonly HttpClient _http;

    public WikipediaFetcher(HttpClient http) => _http = http;

    public async Task<ResultadoScrapeo?> FetchAsync(string url)
    {
        using var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        Console.WriteLine(
    $"[HTTP] {response.Content.Headers.ContentType}");

        // Dejamos que HttpClient interprete correctamente
        // la codificación declarada por Wikipedia.
        var bytes = await response.Content.ReadAsByteArrayAsync();



        var doc = new HtmlDocument();

using var stream = new MemoryStream(bytes);

doc.Load(stream, Encoding.UTF8);

        var titulo = doc.DocumentNode
            .SelectSingleNode("//h1[@id='firstHeading']")
            ?.InnerText?.Trim() ?? "(sin titulo)";

        var contenedor = doc.DocumentNode
            .SelectSingleNode("//div[@id='mw-content-text']");

        if (contenedor == null)
            return null;

        // Quitamos elementos que no aportan texto útil:
        // tablas, referencias, notas y cajas de navegación.
        var nodosAEliminar = contenedor.SelectNodes(
            ".//table | .//sup[contains(@class,'reference')] | .//div[contains(@class,'navbox')] " +
            "| .//div[contains(@class,'reflist')] | .//ol[contains(@class,'references')] " +
            "| .//div[contains(@class,'infobox')] | .//style | .//script");

        if (nodosAEliminar != null)
        {
            foreach (var nodo in nodosAEliminar)
                nodo.Remove();
        }

        var parrafos = contenedor.SelectNodes(".//p");

        if (parrafos == null)
            return null;

        var sb = new StringBuilder();

        foreach (var p in parrafos)
        {
            var texto =
                HtmlEntity.DeEntitize(p.InnerText).Trim();

            texto = Regex.Replace(
                texto,
                @"\[\d+\]",
                "");

            texto = Regex.Replace(
                texto,
                @"\s+",
                " ").Trim();

            if (texto.Length > 30)
                sb.AppendLine(texto);
        }

        var contenido =
            sb.ToString().Trim();

        return contenido.Length < 100
            ? null
            : new ResultadoScrapeo(
                titulo,
                contenido);
    }
}