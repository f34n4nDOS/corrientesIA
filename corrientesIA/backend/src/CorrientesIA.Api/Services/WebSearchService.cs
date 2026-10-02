using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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

        var consultaNormalizada =
            NormalizarTexto(consulta);

        var url =
            $"search?q={Uri.EscapeDataString(consulta)}" +
            "&format=json&language=es";

        // ============================================================
        // SEARXNG
        // ============================================================

        using var ctsSearx =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        ctsSearx.CancelAfter(
            TimeSpan.FromSeconds(4));

        SearxResponse? respuesta;

        try
        {
            respuesta =
                await _http.GetFromJsonAsync<SearxResponse>(
                    url,
                    ctsSearx.Token);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (HttpRequestException)
        {
            return [];
        }

        var resultados =
            respuesta?.Results ?? [];

        if (resultados.Count == 0)
            return [];

        // ============================================================
        // CLASIFICACIÓN INICIAL
        // ============================================================

        foreach (var resultado in resultados)
        {
            resultado.TipoFuente =
                ClasificarFuente(resultado.Url);

            resultado.PrioridadFuente =
                ObtenerPrioridadFuente(
                    resultado.TipoFuente);
        }

        // ============================================================
        // RELEVANCIA INICIAL
        //
        // Se calcula antes de descargar HTML para evitar gastar
        // tiempo enriqueciendo resultados poco relevantes.
        // ============================================================

        foreach (var resultado in resultados)
        {
            resultado.RelevanciaConsulta =
                CalcularRelevancia(
                    resultado,
                    consultaNormalizada);
        }

        // ============================================================
        // ENRIQUECIMIENTO
        //
        // Seleccionamos los mejores candidatos por relevancia
        // combinando:
        //
        // - relevancia textual
        // - prioridad de fuente
        // - score original de SearXNG
        //
        // Se mantienen solo 3 para conservar la latencia baja.
        // ============================================================

        var candidatosAEnriquecer =
            resultados
                .OrderByDescending(
                    r => r.RelevanciaConsulta)
                .ThenByDescending(
                    r => r.PrioridadFuente)
                .ThenByDescending(
                    r => r.Score)
                .Take(3)
                .ToList();

        var tareasEnriquecimiento =
            candidatosAEnriquecer.Select(
                async resultado =>
                {
                    using var ctsPagina =
                        CancellationTokenSource
                            .CreateLinkedTokenSource(
                                cancellationToken);

                    ctsPagina.CancelAfter(
                        TimeSpan.FromSeconds(3));

                    var descripcionOg =
                        await ObtenerDescripcionOgAsync(
                            resultado.Url,
                            ctsPagina.Token);

                    if (!string.IsNullOrWhiteSpace(
                            descripcionOg))
                    {
                        resultado.Content =
                            descripcionOg;
                    }

                    // Recalcular porque el contenido
                    // puede haber cambiado después
                    // del enriquecimiento.
                    resultado.RelevanciaConsulta =
                        CalcularRelevancia(
                            resultado,
                            consultaNormalizada);
                });

        await Task.WhenAll(
            tareasEnriquecimiento);

        // ============================================================
        // ORDEN FINAL
        //
        // Para consultas normales se conserva la prioridad de fuente.
        //
        // Para consultas históricas / de creación / fundación,
        // la relevancia de la evidencia pasa a tener mayor peso.
        // ============================================================

        bool consultaHistorica =
            EsConsultaHistorica(
                consultaNormalizada);

        if (consultaHistorica)
        {
            return resultados
                .OrderByDescending(
                    r => r.RelevanciaEspecifica)
                .ThenByDescending(
                    r => r.RelevanciaConsulta)
                .ThenByDescending(
                    r => r.PrioridadFuente)
                .ThenByDescending(
                    r => r.Score)
                .ToList();
        }

        return resultados
            .OrderByDescending(
                r => r.PrioridadFuente)
            .ThenByDescending(
                r => r.RelevanciaConsulta)
            .ThenByDescending(
                r => r.Score)
            .ToList();
    }

    // ================================================================
    // RELEVANCIA
    // ================================================================

    private static int CalcularRelevancia(
        WebSearchResult resultado,
        string consultaNormalizada)
    {
        var titulo =
            NormalizarTexto(
                resultado.Title);

        var contenido =
            NormalizarTexto(
                resultado.Content);

        if (string.IsNullOrWhiteSpace(
                titulo) &&
            string.IsNullOrWhiteSpace(
                contenido))
        {
            return 0;
        }

        var palabrasConsulta =
            consultaNormalizada
                .Split(
                    ' ',
                    StringSplitOptions
                        .RemoveEmptyEntries)
                .Where(
                    p => p.Length >= 4)
                .Distinct()
                .ToList();

        int coincidenciasTitulo =
            palabrasConsulta.Count(
                palabra =>
                    ContienePalabra(
                        titulo,
                        palabra));

        int coincidenciasContenido =
            palabrasConsulta.Count(
                palabra =>
                    ContienePalabra(
                        contenido,
                        palabra));

        int relevancia =
            (coincidenciasTitulo * 100) +
            coincidenciasContenido;

        // ============================================================
        // CONSULTAS DE CREACIÓN / FUNDACIÓN / ORIGEN
        // ============================================================

        if (EsConsultaCreacion(
                consultaNormalizada))
        {
            var evidenciaCreacion =
                CalcularRelevanciaCreacion(
                    titulo,
                    contenido);

            relevancia += evidenciaCreacion;

            resultado.RelevanciaEspecifica =
                evidenciaCreacion;
        }
        else
        {
            resultado.RelevanciaEspecifica =
                relevancia;
        }

        return relevancia;
    }

    private static int CalcularRelevanciaCreacion(
    string titulo,
    string contenido)
{
    int puntaje = 0;

    // ============================================================
    // EVIDENCIA DIRECTA DE CREACIÓN / FUNDACIÓN
    //
    // Estas expresiones sí responden directamente a una consulta
    // del tipo "cuándo se creó", "cuándo se fundó", etc.
    // ============================================================

    if (ContieneFrase(
            contenido,
            "se creo"))
    {
        puntaje += 300;
    }

    if (ContieneFrase(
            contenido,
            "fue creada"))
    {
        puntaje += 300;
    }

    if (ContieneFrase(
            contenido,
            "fue creado"))
    {
        puntaje += 300;
    }

    if (ContieneFrase(
            contenido,
            "se fundo"))
    {
        puntaje += 300;
    }

    if (ContieneFrase(
            contenido,
            "fue fundada"))
    {
        puntaje += 300;
    }

    if (ContieneFrase(
            contenido,
            "fue fundado"))
    {
        puntaje += 300;
    }

    if (ContieneFrase(
            contenido,
            "fecha de fundacion"))
    {
        puntaje += 300;
    }

    if (ContieneFrase(
            contenido,
            "ano de fundacion"))
    {
        puntaje += 300;
    }

    if (ContieneFrase(
            contenido,
            "anio de fundacion"))
    {
        puntaje += 300;
    }

    if (ContieneFrase(
            contenido,
            "ano de creacion"))
    {
        puntaje += 300;
    }

    if (ContieneFrase(
            contenido,
            "anio de creacion"))
    {
        puntaje += 300;
    }

    // ============================================================
    // EVIDENCIA HISTÓRICA SECUNDARIA
    //
    // Sirve para encontrar fuentes relacionadas, pero NO debe
    // superar a una fuente que realmente diga que fue creada/fundada.
    // ============================================================

    if (ContieneFrase(
            contenido,
            "primera fiesta nacional del chamame"))
    {
        puntaje += 50;
    }

    if (ContieneFrase(
            contenido,
            "primera edicion"))
    {
        puntaje += 40;
    }

    if (ContieneFrase(
            contenido,
            "origen"))
    {
        puntaje += 30;
    }

    if (ContieneFrase(
            contenido,
            "nacio"))
    {
        puntaje += 30;
    }

    if (ContieneFrase(
            contenido,
            "inicio"))
    {
        puntaje += 20;
    }

    if (ContieneFrase(
            contenido,
            "comenzo"))
    {
        puntaje += 20;
    }

    if (ContieneFrase(
            contenido,
            "primera"))
    {
        puntaje += 20;
    }

    // ============================================================
    // FECHA
    // ============================================================

    if (ContieneAno(
            contenido))
    {
        puntaje += 20;
    }

    // ============================================================
    // TÍTULO HISTÓRICO
    // ============================================================

    if (ContieneFrase(
            titulo,
            "primera fiesta"))
    {
        puntaje += 30;
    }

    if (ContieneFrase(
            titulo,
            "historia"))
    {
        puntaje += 20;
    }

    if (ContieneFrase(
            titulo,
            "origen"))
    {
        puntaje += 20;
    }

    if (ContieneFrase(
            titulo,
            "nacio"))
    {
        puntaje += 20;
    }

    return puntaje;
}

    // ================================================================
    // CONSULTAS HISTÓRICAS
    // ================================================================

    private static bool EsConsultaHistorica(
        string n)
    {
        return
            EsConsultaCreacion(n) ||
            ContieneAlguna(
                n,
                "historia",
                "origen",
                "nacio",
                "primera edicion",
                "primera fiesta");
    }

    private static bool EsConsultaCreacion(
        string n)
    {
        return
            ContieneAlguna(
                n,
                "se creo",
                "se fundo",
                "fue creado",
                "fue creada",
                "fue fundado",
                "fue fundada",
                "fecha de fundacion",
                "ano de fundacion",
                "anio de fundacion",
                "ano de creacion",
                "anio de creacion",
                "cuando se creo",
                "cuando se fundo",
                "origen de");
    }

    // ================================================================
    // FECHAS
    // ================================================================

    private static bool ContieneAno(
        string texto)
    {
        return Regex.IsMatch(
            texto,
            @"\b(1[5-9]\d{2}|20\d{2}|21\d{2})\b");
    }

    // ================================================================
    // DESCRIPCIÓN OG
    // ================================================================

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
                Regex.Match(
                    html,
                    "<meta\\s+property=\"og:description\"\\s+content=\"([^\"]+)\"",
                    RegexOptions.IgnoreCase);

            if (match.Success)
            {
                return
                    System.Net.WebUtility.HtmlDecode(
                        match.Groups[1].Value).Trim();
            }

            // Algunas fuentes oficiales no tienen
            // og:description. Intentamos recuperar
            // información estructurada conocida.

            var gobernadorMatch =
                Regex.Match(
                    html,
                    @"<h2[^>]*class=""[^""]*nombre-persona-organismo[^""]*""[^>]*>\s*(.*?)\s*</h2>\s*<div[^>]*class=""[^""]*cargo-persona-organismo[^""]*""[^>]*>\s*(.*?)\s*</div>",
                    RegexOptions.IgnoreCase |
                    RegexOptions.Singleline);

            if (gobernadorMatch.Success)
            {
                var nombre =
                    System.Net.WebUtility.HtmlDecode(
                        gobernadorMatch.Groups[1].Value)
                        .Trim();

                var cargo =
                    System.Net.WebUtility.HtmlDecode(
                        gobernadorMatch.Groups[2].Value)
                        .Trim();

                return
                    $"{nombre} — {cargo}";
            }

            return null;
        }
        catch
        {
            // Un sitio que falla no debe detener
            // toda la búsqueda.
            return null;
        }
    }

    // ================================================================
    // CLASIFICACIÓN DE FUENTES
    // ================================================================

    private static string ClasificarFuente(
        string url)
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
                .Replace(
                    "www.",
                    "");

        // ------------------------------------------------------------
        // OFICIALES
        // ------------------------------------------------------------

        if (
            dominio.EndsWith(".gob.ar") ||
            dominio == "argentina.gob.ar" ||
            dominio.EndsWith(".gov") ||
            dominio.EndsWith(".gov.uk") ||
            dominio.EndsWith(".gov.au") ||
            dominio.EndsWith(".gov.ca") ||
            dominio.EndsWith(".gouv.fr") ||
            dominio == "elysee.fr")
        {
            return "OFICIAL";
        }

        // ------------------------------------------------------------
        // EDUCATIVAS / INSTITUCIONALES
        // ------------------------------------------------------------

        if (
            dominio.EndsWith(".edu.ar") ||
            dominio.EndsWith(".edu"))
        {
            return "INSTITUCIONAL";
        }

        // ------------------------------------------------------------
        // WIKIPEDIA
        // ------------------------------------------------------------

        if (
            dominio == "wikipedia.org" ||
            dominio.EndsWith(
                ".wikipedia.org"))
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

    // ================================================================
    // NORMALIZACIÓN
    // ================================================================

    private static string NormalizarTexto(
        string texto)
    {
        if (string.IsNullOrWhiteSpace(
                texto))
        {
            return string.Empty;
        }

        var normalizado =
            texto.Normalize(
                System.Text.NormalizationForm.FormD);

        var resultado =
            new System.Text.StringBuilder(
                normalizado.Length);

        foreach (var caracter
                 in normalizado)
        {
            var categoria =
                System.Globalization.CharUnicodeInfo
                    .GetUnicodeCategory(
                        caracter);

            if (categoria ==
                System.Globalization.UnicodeCategory
                    .NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(
                    caracter))
            {
                resultado.Append(
                    char.ToLowerInvariant(
                        caracter));
            }
            else
            {
                resultado.Append(' ');
            }
        }

        return string.Join(
            ' ',
            resultado
                .ToString()
                .Split(
                    ' ',
                    StringSplitOptions
                        .RemoveEmptyEntries));
    }

    // ================================================================
    // UTILIDADES DE TEXTO
    // ================================================================

    private static bool ContieneAlguna(
        string texto,
        params string[] frases)
    {
        foreach (var frase in frases)
        {
            if (ContieneFrase(
                    texto,
                    frase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContieneFrase(
        string texto,
        string frase)
    {
        if (string.IsNullOrWhiteSpace(
                texto) ||
            string.IsNullOrWhiteSpace(
                frase))
        {
            return false;
        }

        var textoNormalizado =
            NormalizarTexto(texto);

        var fraseNormalizada =
            NormalizarTexto(frase);

        if (string.IsNullOrWhiteSpace(
                textoNormalizado) ||
            string.IsNullOrWhiteSpace(
                fraseNormalizada))
        {
            return false;
        }

        return textoNormalizado
            .Contains(
                fraseNormalizada,
                StringComparison.Ordinal);
    }

    private static bool ContienePalabra(
        string texto,
        string palabra)
    {
        if (string.IsNullOrWhiteSpace(
                texto) ||
            string.IsNullOrWhiteSpace(
                palabra))
        {
            return false;
        }

        return
            $" {texto} "
                .Contains(
                    $" {palabra} ",
                    StringComparison.Ordinal);
    }

    // ================================================================
    // RESPUESTA SEARXNG
    // ================================================================

    private sealed class SearxResponse
    {
        [JsonPropertyName("results")]
        public List<WebSearchResult> Results { get; set; } = [];
    }
}

// ====================================================================
// RESULTADO WEB
// ====================================================================

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

    public string TipoFuente { get; set; } =
        "GENERAL";

    public int PrioridadFuente { get; set; }

    public int RelevanciaConsulta { get; set; }

    public int RelevanciaEspecifica { get; set; }
}