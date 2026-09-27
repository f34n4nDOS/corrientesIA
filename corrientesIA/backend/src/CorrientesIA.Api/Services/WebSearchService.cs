using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace CorrientesIA.Api.Services;

public class WebSearchService
{
    private readonly HttpClient _http;

    public WebSearchService(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<WebSearchResult>> BuscarAsync(
        string consulta,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(consulta))
            return [];

        var url =
            $"search?q={Uri.EscapeDataString(consulta)}" +
            "&format=json&language=es";

        var respuesta =
            await _http.GetFromJsonAsync<SearxResponse>(
                url,
                cancellationToken);

        var resultados = respuesta?.Results ?? [];

        if (resultados.Count == 0)
            return [];

        // Normalizar y clasificar las fuentes.
        foreach (var resultado in resultados)
        {
            resultado.TipoFuente =
                ClasificarFuente(resultado.Url);

            resultado.PrioridadFuente =
                ObtenerPrioridadFuente(resultado.TipoFuente);
        }

        // Separar la consulta en palabras significativas.
        var palabrasConsulta = consulta
            .ToLowerInvariant()
            .Split(
                new[] { ' ', ',', '.', ';', ':', '?', '¿', '!', '¡' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Length >= 4)
            .Distinct()
            .ToList();

        // Calcular relevancia.
        foreach (var resultado in resultados)
        {
            var titulo =
                resultado.Title.ToLowerInvariant();

            var contenido =
                resultado.Content.ToLowerInvariant();

            var coincidenciasTitulo =
                palabrasConsulta.Count(
                    palabra => titulo.Contains(palabra));

            var coincidenciasContenido =
                palabrasConsulta.Count(
                    palabra => contenido.Contains(palabra));

            /*
             * El título pesa mucho más que el contenido.
             *
             * Ejemplo:
             *
             * "Capital de Francia - Wikipedia"
             * → encuentra "capital" y "francia" en el título
             * → 2 * 100 = 200
             *
             * "París - Wikipedia"
             * → puede mencionar ambas palabras solamente
             *    dentro del contenido
             * → 2
             *
             * Esto permite distinguir un resultado específico
             * de uno que solamente menciona el tema.
             */
            resultado.RelevanciaConsulta =
                (coincidenciasTitulo * 100) +
                coincidenciasContenido;
        }

        // Orden final:
        //
        // 1. Fuente oficial
        // 2. Fuente institucional
        // 3. Enciclopedia
        // 4. Fuente general
        //
        // Dentro de cada categoría:
        //
        // 1. Coincidencia en título
        // 2. Coincidencia en contenido
        // 3. Score original de SearXNG
        return resultados
    .OrderByDescending(r => r.RelevanciaConsulta)
    .ThenByDescending(r => r.PrioridadFuente)
    .ThenByDescending(r => r.Score)
    .ToList();
    }

    private static string ClasificarFuente(string url)
    {
        if (!Uri.TryCreate(
                url,
                UriKind.Absolute,
                out var uri))
        {
            return "GENERAL";
        }

        var dominio =
            uri.Host
                .ToLowerInvariant()
                .Replace("www.", "");

        // Fuentes oficiales.
        if (
            dominio.EndsWith(".gob.ar") ||
            dominio == "argentina.gob.ar" ||
            dominio.EndsWith(".gov") ||
            dominio.EndsWith(".gov.uk") ||
            dominio.EndsWith(".gov.au") ||
            dominio.EndsWith(".gov.ca") ||
            dominio.EndsWith(".gouv.fr") ||
            dominio == "elysee.fr"
        )
        {
            return "OFICIAL";
        }

        // Fuentes educativas / institucionales.
        if (
            dominio.EndsWith(".edu.ar") ||
            dominio.EndsWith(".edu")
        )
        {
            return "INSTITUCIONAL";
        }

        // Wikipedia.
        if (
            dominio == "wikipedia.org" ||
            dominio.EndsWith(".wikipedia.org")
        )
        {
            return "ENCICLOPEDIA";
        }

        return "GENERAL";
    }

    private static int ObtenerPrioridadFuente(
        string tipoFuente)
    {
        return tipoFuente switch
        {
            "OFICIAL" => 4,
            "INSTITUCIONAL" => 3,
            "ENCICLOPEDIA" => 2,
            _ => 1
        };
    }

    private sealed class SearxResponse
    {
        [JsonPropertyName("results")]
        public List<WebSearchResult> Results { get; set; } = [];
    }
}

public class WebSearchResult
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("engines")]
    public List<string> Engines { get; set; } = [];

    public string TipoFuente { get; set; } = "GENERAL";

    public int PrioridadFuente { get; set; }

    public int RelevanciaConsulta { get; set; }
}

