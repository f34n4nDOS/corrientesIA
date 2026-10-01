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

        // Timeout duro para SearXNG en si: si no responde rapido, mejor
        // devolver vacio (y que el caller siga con otro fallback) que
        // colgar toda la respuesta del chat esperando indefinidamente.
        using var ctsSearx = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ctsSearx.CancelAfter(TimeSpan.FromSeconds(4));

        SearxResponse? respuesta;
        try
        {
            respuesta = await _http.GetFromJsonAsync<SearxResponse>(url, ctsSearx.Token);
        }
        catch (OperationCanceledException)
        {
            return []; // SearXNG no respondio en 4s
        }
        catch (HttpRequestException)
        {
            return []; // SearXNG caido o inalcanzable
        }

        var resultados = respuesta?.Results ?? [];

        if (resultados.Count == 0)
            return [];

        // Clasificar y priorizar TODOS los resultados primero (esto es
        // barato, no pega a la red): asi elegimos a cuales vale la pena
        // pedirles el HTML completo antes de gastar tiempo en eso.
        foreach (var resultado in resultados)
        {
            resultado.TipoFuente =
                ClasificarFuente(resultado.Url);

            resultado.PrioridadFuente =
                ObtenerPrioridadFuente(resultado.TipoFuente);
        }

        // Antes se pedia el HTML completo de TODOS los resultados, uno por
        // uno, en secuencia (await dentro de un foreach) — con 8-10
        // resultados de SearXNG y algun sitio lento en el medio, eso
        // explica los 48-56s de latencia observados en las pruebas. Ahora:
        // (a) solo enriquecemos los candidatos mas prometedores segun la
        // prioridad de fuente + score que YA nos da SearXNG sin pegarle a
        // la red, y (b) los pedimos en PARALELO, cada uno con su propio
        // timeout corto, asi un sitio colgado no frena a los demas.
        var candidatosAEnriquecer = resultados
            .OrderByDescending(r => r.PrioridadFuente)
            .ThenByDescending(r => r.Score)
            .Take(3)
            .ToList();

        var tareasEnriquecimiento = candidatosAEnriquecer.Select(async resultado =>
        {
            using var ctsPagina = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ctsPagina.CancelAfter(TimeSpan.FromSeconds(3));

            var descripcionOg =
                await ObtenerDescripcionOgAsync(resultado.Url, ctsPagina.Token);

            if (!string.IsNullOrWhiteSpace(descripcionOg))
                resultado.Content = descripcionOg;
        });

        await Task.WhenAll(tareasEnriquecimiento);

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
            .OrderByDescending(r => r.PrioridadFuente)
            .ThenByDescending(r => r.RelevanciaConsulta)
            .ThenByDescending(r => r.Score)
            .ToList();
    }

    private async Task<string?> ObtenerDescripcionOgAsync(
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            var html =
                await _http.GetStringAsync(
                    url,
                    cancellationToken);

            var match =
                System.Text.RegularExpressions.Regex.Match(
                    html,
                    "<meta\\s+property=\"og:description\"\\s+content=\"([^\"]+)\"",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success)
                return match.Groups[1].Value;

            // Algunas fuentes oficiales no tienen og:description.
            // Intentamos obtener información estructurada directamente del HTML.
            var gobernadorMatch =
                System.Text.RegularExpressions.Regex.Match(
                    html,
                    @"<h2[^>]*class=""[^""]*nombre-persona-organismo[^""]*""[^>]*>\s*(.*?)\s*</h2>\s*<div[^>]*class=""[^""]*cargo-persona-organismo[^""]*""[^>]*>\s*(.*?)\s*</div>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                    System.Text.RegularExpressions.RegexOptions.Singleline);

            if (gobernadorMatch.Success)
            {
                var nombre =
                    System.Net.WebUtility.HtmlDecode(
                        gobernadorMatch.Groups[1].Value).Trim();

                var cargo =
                    System.Net.WebUtility.HtmlDecode(
                        gobernadorMatch.Groups[2].Value).Trim();

                return $"{nombre} — {cargo}";
            }

            return null;
        }
        catch
        {
            // Incluye timeouts (OperationCanceledException), DNS, 404, etc.
            // Un sitio que falla acá simplemente no se enriquece; el
            // snippet original de SearXNG sigue disponible.
            return null;
        }
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
