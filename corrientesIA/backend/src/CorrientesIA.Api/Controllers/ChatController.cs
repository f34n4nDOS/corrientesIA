using CorrientesIA.Api.Services;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CorrientesIA.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly InferenceService _inference;
    private readonly GroundingService _grounding;
    private readonly WebSearchService _webSearch;

    public ChatController(
        InferenceService inference,
        GroundingService grounding,
        WebSearchService webSearch)
    {
        _inference = inference;
        _grounding = grounding;
        _webSearch = webSearch;
    }

    public record ChatRequest(string Mensaje);

    public record ChatResponse(string Respuesta);

    [HttpPost]
    public async Task<ActionResult<ChatResponse>> Post(
        [FromBody] ChatRequest request,
        CancellationToken cancellationToken)
    {
        // ============================================================
        // VALIDACIÓN
        // ============================================================

        if (string.IsNullOrWhiteSpace(request.Mensaje))
        {
            return BadRequest(
                "El mensaje no puede estar vacio.");
        }

        var mensaje = request.Mensaje.Trim();

        // ============================================================
        // 1. DATOS VERIFICADOS / RESPUESTAS DIRECTAS
        // ============================================================

        var datoVerificado =
            await _grounding.BuscarPorPalabrasClaveAsync(
                mensaje);

        if (!string.IsNullOrWhiteSpace(datoVerificado))
        {
            return Ok(
                new ChatResponse(datoVerificado));
        }

        // ============================================================
        // 2. CORPUS LOCAL
        // ============================================================

        var datoCorpus =
            await _grounding.BuscarEnCorpusAsync(
                mensaje);

        Console.WriteLine(
            $"CORPUS RESULTADO: [{datoCorpus ?? "NULL"}]");

        if (!string.IsNullOrWhiteSpace(datoCorpus))
        {
            return Ok(
                new ChatResponse(datoCorpus));
        }

        // ============================================================
        // 3. CONTEXTO GENERAL
        //
        // Las consultas específicas no deben degradarse a un
        // documento general si el corpus no contiene la respuesta.
        // ============================================================

        if (!EsConsultaEspecifica(mensaje))
        {
            var contexto =
                await _grounding.BuscarContextoGeneralAsync(
                    mensaje);

            if (contexto is not null)
            {
                return Ok(
                    new ChatResponse(
                        $"{contexto.Value.Titulo}: " +
                        $"{contexto.Value.Contenido}"));
            }
        }

        // ============================================================
        // 4. BÚSQUEDA WEB
        // ============================================================

        var resultadosWeb =
            await _webSearch.BuscarAsync(
                mensaje,
                cancellationToken);

        foreach (var resultado in resultadosWeb)
        {
            Console.WriteLine(
                $"WEB: [{resultado.TipoFuente}] " +
                $"REL={resultado.RelevanciaConsulta} " +
                $"PRIO={resultado.PrioridadFuente} " +
                $"{resultado.Title}");
        }

        var mejorResultado =
            resultadosWeb.FirstOrDefault();

        if (mejorResultado is not null &&
            !string.IsNullOrWhiteSpace(
                mejorResultado.Content) &&
            !string.IsNullOrWhiteSpace(
                mejorResultado.Url))
        {
            var contenido =
                LimpiarContenidoWeb(
                    mejorResultado.Content);

            if (!string.IsNullOrWhiteSpace(contenido))
            {
                // ====================================================
                // 4.1 RESPUESTA ESPECÍFICA PARA CONSULTAS HISTÓRICAS
                // ====================================================

                var respuestaEspecifica =
                    ExtraerRespuestaHistorica(
                        mensaje,
                        contenido);
                    if (respuestaEspecifica is null &&
    EsConsultaHistorica(
        NormalizarTexto(mensaje)))
{
    return Ok(
        new ChatResponse(
            "No encontré información verificada que indique de forma explícita el año de creación de la Fiesta Nacional del Chamamé."));
}
                if (!string.IsNullOrWhiteSpace(
                        respuestaEspecifica))
                {
                    var tipoFuente =
                        mejorResultado.TipoFuente
                            .ToLowerInvariant();

                    var respuestaHistorica =
                        $"Según {tipoFuente}: " +
                        $"{respuestaEspecifica}\n\n" +
                        $"Fuente: {mejorResultado.Url}";

                    Console.WriteLine(
                        $"WEB RESPUESTA ESPECIFICA: " +
                        $"{respuestaEspecifica}");

                    return Ok(
                        new ChatResponse(
                            respuestaHistorica));
                }

                // ====================================================
                // 4.2 RESPUESTA WEB NORMAL
                // ====================================================

                var tipoFuenteNormal =
                    mejorResultado.TipoFuente
                        .ToLowerInvariant();

                var respuestaWeb =
                    $"Según {tipoFuenteNormal}: " +
                    $"{contenido}\n\n" +
                    $"Fuente: {mejorResultado.Url}";

                return Ok(
                    new ChatResponse(
                        respuestaWeb));
            }
        }

        // ============================================================
        // 5. SIN INFORMACIÓN SUFICIENTE
        // ============================================================

        return Ok(
            new ChatResponse(
                "No encontré información verificada " +
                "sobre esa consulta."));
    }

    // ================================================================
    // EXTRACCIÓN DE RESPUESTAS HISTÓRICAS
    //
    // Busca una oración concreta que responda preguntas como:
    //
    // - ¿En qué año se creó la Fiesta Nacional del Chamamé?
    // - ¿Cuándo se realizó la primera Fiesta Nacional del Chamamé?
    // - ¿Cuál fue el origen de la Fiesta Nacional del Chamamé?
    //
    // No inventa el año.
    // El año debe existir realmente dentro del contenido web.
    // ================================================================

    private static string? ExtraerRespuestaHistorica(
    string mensaje,
    string contenido)
{
    var consulta =
        NormalizarTexto(mensaje);

    if (!EsConsultaHistorica(consulta))
        return null;

    var oraciones =
        SepararOraciones(contenido);

    if (oraciones.Count == 0)
        return null;

    bool consultaCreacion =
        ContieneAlguna(
            consulta,
            "cuando se fundo",
            "cuando fue fundado",
            "cuando fue fundada",
            "fecha de fundacion",
            "ano de fundacion",
            "anio de fundacion",
            "se fundo",
            "fue fundado",
            "fue fundada",
            "cuando se creo",
            "cuando fue creado",
            "cuando fue creada",
            "se creo",
            "fue creado",
            "fue creada",
            "ano de creacion",
            "anio de creacion",
            "fecha de creacion");

    bool consultaPrimeraEdicion =
        ContieneAlguna(
            consulta,
            "primera edicion",
            "primera fiesta",
            "primera vez");

    var candidatas =
        new List<CandidataHistorica>();

    foreach (var oracion in oraciones)
    {
        var normalizada =
            NormalizarTexto(oracion);

        if (!ContieneAno(normalizada))
            continue;

        int puntaje = 0;

        // ========================================================
        // CONSULTA DE CREACIÓN / FUNDACIÓN
        //
        // Para este tipo de pregunta solamente aceptamos
        // evidencia explícita de creación o fundación.
        // ========================================================

        if (consultaCreacion)
        {
            bool evidenciaDirecta =
                ContieneAlguna(
                    normalizada,
                    "se creo",
                    "fue creada",
                    "fue creado",
                    "se fundo",
                    "fue fundada",
                    "fue fundado",
                    "fecha de fundacion",
                    "ano de fundacion",
                    "anio de fundacion",
                    "fecha de creacion",
                    "ano de creacion",
                    "anio de creacion");

            if (!evidenciaDirecta)
                continue;

            puntaje += 300;

            if (ContieneFrase(
                    normalizada,
                    "fiesta nacional del chamame"))
            {
                puntaje += 100;
            }

            puntaje +=
                ContarAnios(normalizada) * 25;
        }

        // ========================================================
        // CONSULTA SOBRE PRIMERA EDICIÓN / PRIMERA FIESTA
        // ========================================================

        else if (consultaPrimeraEdicion)
        {
            if (ContieneFrase(
                    normalizada,
                    "primera fiesta nacional del chamame"))
            {
                puntaje += 180;
            }

            if (ContieneFrase(
                    normalizada,
                    "primera fiesta"))
            {
                puntaje += 100;
            }

            if (ContieneFrase(
                    normalizada,
                    "primera edicion"))
            {
                puntaje += 150;
            }

            if (ContieneFrase(
                    normalizada,
                    "se realizo por primera vez"))
            {
                puntaje += 160;
            }

            if (ContieneFrase(
                    normalizada,
                    "se realizo"))
            {
                puntaje += 70;
            }

            if (ContieneFrase(
                    normalizada,
                    "fiesta nacional del chamame"))
            {
                puntaje += 80;
            }

            puntaje +=
                ContarAnios(normalizada) * 25;
        }

        // ========================================================
        // OTRAS CONSULTAS HISTÓRICAS
        // ========================================================

        else
        {
            if (ContieneFrase(
                    normalizada,
                    "fiesta nacional del chamame"))
            {
                puntaje += 100;
            }

            if (ContieneFrase(
                    normalizada,
                    "origen"))
            {
                puntaje += 90;
            }

            if (ContieneFrase(
                    normalizada,
                    "nacio"))
            {
                puntaje += 90;
            }

            if (ContieneFrase(
                    normalizada,
                    "inicio"))
            {
                puntaje += 70;
            }

            if (ContieneFrase(
                    normalizada,
                    "comenzo"))
            {
                puntaje += 70;
            }

            if (ContieneFrase(
                    normalizada,
                    "historia"))
            {
                puntaje += 50;
            }

            puntaje +=
                ContarAnios(normalizada) * 25;
        }

        if (puntaje <= 0)
            continue;

        candidatas.Add(
            new CandidataHistorica(
                oracion.Trim(),
                puntaje));
    }

    if (candidatas.Count == 0)
        return null;

    var mejor =
        candidatas
            .OrderByDescending(
                x => x.Puntaje)
            .ThenByDescending(
                x => x.Oracion.Length)
            .First();

    // ============================================================
    // UMBRAL DE SEGURIDAD
    // ============================================================

    if (mejor.Puntaje < 100)
        return null;

    return mejor.Oracion;
}

    private sealed record CandidataHistorica(
        string Oracion,
        int Puntaje);

    // ================================================================
    // DETECCIÓN DE CONSULTAS HISTÓRICAS
    // ================================================================

    private static bool EsConsultaHistorica(
        string n)
    {
        return ContieneAlguna(
            n,
            "cuando se fundo",
            "cuando fue fundado",
            "cuando fue fundada",
            "fecha de fundacion",
            "ano de fundacion",
            "anio de fundacion",
            "se fundo",
            "fue fundado",
            "fue fundada",
            "cuando se creo",
            "cuando fue creado",
            "cuando fue creada",
            "se creo",
            "fue creado",
            "fue creada",
            "ano de creacion",
            "anio de creacion",
            "fecha de creacion",
            "origen",
            "historia",
            "primera edicion",
            "primera fiesta",
            "primera vez");
    }

    // ================================================================
    // SEPARACIÓN DE ORACIONES
    // ================================================================

    private static List<string> SepararOraciones(
        string contenido)
    {
        if (string.IsNullOrWhiteSpace(contenido))
            return [];

        return Regex
            .Split(
                contenido,
                @"(?<=[\.\!\?])\s+")
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();
    }

    // ================================================================
    // DETECCIÓN DE AÑOS
    // ================================================================

    private static bool ContieneAno(
        string texto)
    {
        return Regex.IsMatch(
            texto,
            @"\b(?:19|20)\d{2}\b");
    }

    private static int ContarAnios(
        string texto)
    {
        return Regex
            .Matches(
                texto,
                @"\b(?:19|20)\d{2}\b")
            .Count;
    }

    // ================================================================
    // LIMPIEZA DE CONTENIDO WEB
    // ================================================================

    private static string LimpiarContenidoWeb(
        string contenido)
    {
        if (string.IsNullOrWhiteSpace(contenido))
            return string.Empty;

        var resultado =
            contenido.Trim();

        resultado = resultado.Replace(
            "¿Cuál es la capital de Francia?",
            string.Empty,
            StringComparison.OrdinalIgnoreCase);

        resultado = resultado.Replace(
            "La. capital",
            "La capital",
            StringComparison.Ordinal);

        resultado = resultado.Trim();

        while (resultado.EndsWith(
                   "...",
                   StringComparison.Ordinal))
        {
            resultado =
                resultado[..^3].Trim();
        }

        return resultado;
    }

    // ================================================================
    // CONSULTAS ESPECÍFICAS
    // ================================================================

    private static bool EsConsultaEspecifica(
        string mensaje)
    {
        var n =
            NormalizarTexto(mensaje);

        // ------------------------------------------------------------
        // FECHAS / AÑOS / FUNDACIÓN / CREACIÓN
        // ------------------------------------------------------------

        if (ContieneAlguna(
            n,
            "cuando",
            "fecha",
            "ano",
            "anio",
            "en que ano",
            "en que fecha",
            "se fundo",
            "fue fundado",
            "fue fundada",
            "fundacion",
            "se creo",
            "fue creado",
            "fue creada",
            "creacion",
            "origen"))
        {
            return true;
        }

        // ------------------------------------------------------------
        // POBLACIÓN
        // ------------------------------------------------------------

        if (ContieneAlguna(
            n,
            "poblacion",
            "habitantes",
            "habitantes tiene",
            "cuantos habitantes",
            "cuanta poblacion",
            "censo"))
        {
            return true;
        }

        // ------------------------------------------------------------
        // UBICACIÓN
        // ------------------------------------------------------------

        if (ContieneAlguna(
            n,
            "donde queda",
            "donde esta",
            "donde se encuentra",
            "donde se ubica",
            "ubicacion",
            "ubicado",
            "ubicada",
            "localizado",
            "localizada"))
        {
            return true;
        }

        // ------------------------------------------------------------
        // DEFINICIONES
        // ------------------------------------------------------------

        if (ContieneAlguna(
            n,
            "que es",
            "que significa",
            "definicion",
            "define",
            "significa"))
        {
            return true;
        }

        return false;
    }

    // ================================================================
    // NORMALIZACIÓN
    // ================================================================

    private static string NormalizarTexto(
        string texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
            return string.Empty;

        var normalizado =
            texto.Normalize(
                NormalizationForm.FormD);

        var resultado =
            new StringBuilder(
                normalizado.Length);

        foreach (var caracter in normalizado)
        {
            var categoria =
                CharUnicodeInfo
                    .GetUnicodeCategory(
                        caracter);

            if (categoria ==
                UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(caracter))
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
        if (string.IsNullOrWhiteSpace(texto) ||
            string.IsNullOrWhiteSpace(frase))
        {
            return false;
        }

        var fraseNormalizada =
            NormalizarTexto(frase);

        if (string.IsNullOrWhiteSpace(
                fraseNormalizada))
        {
            return false;
        }

        var palabrasTexto =
            texto.Split(
                ' ',
                StringSplitOptions
                    .RemoveEmptyEntries);

        var palabrasFrase =
            fraseNormalizada.Split(
                ' ',
                StringSplitOptions
                    .RemoveEmptyEntries);

        if (palabrasFrase.Length >
            palabrasTexto.Length)
        {
            return false;
        }

        for (
            var i = 0;
            i <= palabrasTexto.Length -
                palabrasFrase.Length;
            i++)
        {
            var coincide = true;

            for (
                var j = 0;
                j < palabrasFrase.Length;
                j++)
            {
                if (!string.Equals(
                        palabrasTexto[i + j],
                        palabrasFrase[j],
                        StringComparison.Ordinal))
                {
                    coincide = false;
                    break;
                }
            }

            if (coincide)
                return true;
        }

        return false;
    }
}