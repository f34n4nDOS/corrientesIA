using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using CorrientesIA.Scraper.Models;

namespace CorrientesIA.Scraper.Fetchers;

// Fetcher generico para sitios de gobierno/turismo que no tienen una
// estructura tan predecible como Wikipedia. Estrategia simple y robusta:
// tomar todos los <p> del documento, filtrar ruido tipico (menus, footers)
// por longitud minima, y listo.
public class GenericHtmlFetcher : IContentFetcher
{
    private readonly HttpClient _http;

    // Tags que casi siempre son navegacion/ruido y no texto de contenido
    private static readonly string[] SelectoresAEliminar =
    {
        "nav", "header", "footer", "script", "style", "aside", "form"
    };

    public GenericHtmlFetcher(HttpClient http) => _http = http;

    public async Task<ResultadoScrapeo?> FetchAsync(string url)
    {
        var html = await _http.GetStringAsync(url);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        foreach (var selector in SelectoresAEliminar)
        {
            var nodos = doc.DocumentNode.SelectNodes($"//{selector}");
            if (nodos == null) continue;
            foreach (var nodo in nodos) nodo.Remove();
        }

        var titulo = doc.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim() ?? url;

        var parrafos = doc.DocumentNode.SelectNodes("//p");
        if (parrafos == null) return null;

        var sb = new StringBuilder();
        foreach (var p in parrafos)
        {
            var texto = HtmlEntity.DeEntitize(p.InnerText).Trim();
            texto = Regex.Replace(texto, @"\s+", " ").Trim();

            // filtro de ruido: parrafos muy cortos suelen ser menus/links sueltos
            if (texto.Length > 40)
                sb.AppendLine(texto);
        }

        var contenido = sb.ToString().Trim();
        return contenido.Length < 100 ? null : new ResultadoScrapeo(titulo, contenido);
    }
}
