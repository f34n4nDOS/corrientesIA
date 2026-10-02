using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CorrientesIA.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CorrientesIA.Api.Services
{
    /// <summary>
    /// Servicio de búsqueda y grounding sobre datos duros,
    /// lugares y documentos del corpus.
    /// </summary>
    public class GroundingService
    {
        private readonly AppDbContext _db;
        private readonly IMemoryCache? _cache;

        // ============================================================
        // CONFIGURACIÓN
        // ============================================================

        private static readonly TimeSpan CacheTtl =
            TimeSpan.FromMinutes(5);

        private const string CacheDocumentos =
            "grounding:documentos";

        private const string CacheDatosDuros =
            "grounding:datosduros";

        private const string CacheLugares =
            "grounding:lugares";

        private const string TituloCiudad =
            "corrientes ciudad";

        private const string TituloProvincia =
            "provincia de corrientes";

        private const int LongitudMinimaValorDatoDuro = 3;

        private const int PuntajeMinimoGeneral = 10;

        private const int MaxOracionesContextoGeneral = 5;

        // ============================================================
        // LOCALIDADES CONOCIDAS
        // ============================================================

        private static readonly string[] Localidades =
        {
            "monte caseros",
            "san luis del palmar",
            "san roque",
            "ituzaingo",
            "mercedes",
            "goya",
            "saladas",
            "alvear",
            "bella vista",
            "curuzu cuatia",
            "esquina",
            "itati",
            "paso de los libres",
            "santo tome"
        };

        // ============================================================
        // TURISMO
        // ============================================================

        private static readonly string[] DocumentosTuristicos =
        {
            "esteros del ibera",
            "parque nacional ibera",
            "parque nacional mburucuya",
            "chamame",
            "fiesta nacional del chamame"
        };

        private static readonly string[] DocumentosTuristicosNaturales =
        {
            "esteros del ibera",
            "parque nacional ibera",
            "parque nacional mburucuya"
        };

        // ============================================================
        // ENTIDADES
        // ============================================================

        private static readonly string[] EntidadesOrdenadas =
            Localidades
                .Concat(
                    new[]
                    {
                        "esteros del ibera",
                        "parque nacional ibera",
                        "parque nacional mburucuya",
                        "fiesta nacional del chamame",
                        "chamame",
                        "rio parana",
                        "paye",
                        "ibera",
                        "mburucuya",
                        "corrientes"
                    })
                .OrderByDescending(e => e.Length)
                .ToArray();

        // ============================================================
        // PALABRAS GENÉRICAS
        // ============================================================

        private static readonly HashSet<string> PalabrasGenericas =
            new(
                new[]
                {
                    "que",
                    "cual",
                    "cuales",
                    "quien",
                    "quienes",
                    "como",
                    "donde",
                    "cuando",
                    "porque",
                    "para",
                    "con",
                    "por",
                    "del",
                    "de",
                    "la",
                    "las",
                    "el",
                    "los",
                    "un",
                    "una",
                    "unos",
                    "unas",
                    "es",
                    "son",
                    "hay",
                    "tiene",
                    "tienen",
                    "informacion",
                    "sobre",
                    "corrientes",
                    "argentina",
                    "provincia",
                    "ciudad",
                    "capital",
                    "lugar",
                    "lugares",
                    "ubicado",
                    "ubicada",
                    "ubicacion",
                    "localizado",
                    "localizada",
                    "localidad",
                    "localidades",
                    "municipio",
                    "municipios",
                    "poblacion",
                    "habitantes",
                    "actual",
                    "actualmente",
                    "vigente",
                    "hoy",
                    "conoce",
                    "sabe"
                },
                StringComparer.Ordinal);

        // ============================================================
        // REGEX
        // ============================================================

        private static readonly Regex RegexAnio =
            new(
                @"\b(1[5-9][0-9]{2}|20[0-9]{2}|2100)\b",
                RegexOptions.Compiled |
                RegexOptions.CultureInvariant);

        private static readonly Regex RegexOraciones =
            new(
                @"(?<=[.!?])\s+|[\r\n]+",
                RegexOptions.Compiled |
                RegexOptions.CultureInvariant);

        private static readonly Regex RegexDigito =
            new(
                @"[0-9]",
                RegexOptions.Compiled |
                RegexOptions.CultureInvariant);

        // ============================================================
        // TIPOS DE ÍNDICE
        // ============================================================

        private sealed record DocumentoIndexado(
            string Titulo,
            string Contenido,
            string TituloNorm,
            string ContenidoNorm,
            HashSet<string> TituloTokens,
            HashSet<string> ContenidoTokens);

        private sealed record DatoDuroIndexado(
            string Clave,
            string Valor,
            string ClaveNorm,
            string[] Palabras,
            string ValorNorm);

        private sealed record LugarIndexado(
            string Nombre,
            string Descripcion,
            string NombreNorm,
            string LocalidadNorm,
            string CategoriaNorm);

        private sealed record OracionPuntuada(
            string Texto,
            int Score);

        // ============================================================
        // CONSTRUCTOR
        // ============================================================

        public GroundingService(
            AppDbContext db,
            IMemoryCache? cache = null)
        {
            _db = db;
            _cache = cache;
        }

        // ============================================================
        // CACHE
        // ============================================================

        /// <summary>
        /// Invalida las cachés después de modificar datos.
        /// </summary>
        public void InvalidarCache()
        {
            _cache?.Remove(CacheDocumentos);
            _cache?.Remove(CacheDatosDuros);
            _cache?.Remove(CacheLugares);
        }

        // ============================================================
        // API PÚBLICA
        // ============================================================

        public async Task<string?> BuscarDatoDuroAsync(
            string consulta)
        {
            if (string.IsNullOrWhiteSpace(consulta))
                return null;

            return await BuscarDatoDuroInternoAsync(
                NormalizarTexto(consulta));
        }

        public async Task<string?> BuscarLugaresAsync(
            string consulta)
        {
            if (string.IsNullOrWhiteSpace(consulta))
                return null;

            return await BuscarLugaresInternoAsync(
                NormalizarTexto(consulta));
        }

        public async Task<string?> BuscarPorPalabrasClaveAsync(
            string consulta)
        {
            if (string.IsNullOrWhiteSpace(consulta))
                return null;

            var n =
                NormalizarTexto(consulta);

            if (EsGobernadorActual(n))
                return null;

            if (EsConsultaFaltante(n))
            {
                return await
                    ConstruirRespuestaSobreFaltantesAsync();
            }

            if (EsMetaConocimiento(n))
            {
                return await
                    ConstruirRespuestaSobreConocimientoAsync();
            }

            var datoDuro =
                await BuscarDatoDuroInternoAsync(n);

            if (!string.IsNullOrWhiteSpace(datoDuro))
                return datoDuro;

            if (EsConsultaInformacionLugar(n) &&
                !EsConsultaUbicacion(n))
            {
                return await
                    BuscarLugaresInternoAsync(n);
            }

            return null;
        }

        // ============================================================
        // CORPUS
        // ============================================================

        public async Task<string?> BuscarEnCorpusAsync(
            string consulta)
        {
            if (string.IsNullOrWhiteSpace(consulta))
                return null;

            var n =
                NormalizarTexto(consulta);

            if (EsGobernadorActual(n))
                return null;

            if (EsConsultaFaltante(n))
            {
                return await
                    ConstruirRespuestaSobreFaltantesAsync();
            }

            if (EsMetaConocimiento(n))
            {
                return await
                    ConstruirRespuestaSobreConocimientoAsync();
            }

            var documentos =
                await ObtenerDocumentosAsync();

            if (documentos.Count == 0)
                return null;

            // ========================================================
            // CIUDAD VS PROVINCIA
            // ========================================================

            if (EsConsultaDiferenciaCiudadProvincia(n))
            {
                return ConstruirRespuestaCiudadVsProvincia(
                    documentos);
            }

            // ========================================================
            // CAPITAL
            // ========================================================

            if (EsConsultaCapital(n))
            {
                return BuscarRespuestaCapital(
                    documentos);
            }

            // ========================================================
            // TURISMO
            // ========================================================

            if (EsConsultaTurismo(n))
            {
                return ConstruirRespuestaListaTuristica(
                    documentos,
                    n);
            }

            // ========================================================
            // MUNICIPIOS
            // ========================================================

            if (EsConsultaMunicipios(n))
            {
                return ConstruirRespuestaListaLocalidades(
                    documentos,
                    "municipios/localidades",
                    ". Esta lista refleja los documentos disponibles actualmente y no " +
                    "necesariamente el registro completo de municipios de la provincia.");
            }

            // ========================================================
            // LOCALIDADES
            // ========================================================

            if (EsConsultaLocalidades(n))
            {
                return ConstruirRespuestaListaLocalidades(
                    documentos,
                    "localidades",
                    ". La lista corresponde a los documentos disponibles actualmente.");
            }

            // ========================================================
            // POBLACIÓN
            // ========================================================

            if (EsConsultaPoblacion(n))
            {
                return BuscarRespuestaPoblacion(
                    documentos,
                    n);
            }

            // ========================================================
            // UBICACIÓN
            // ========================================================

            if (EsConsultaUbicacion(n))
            {
                return BuscarRespuestaUbicacion(
                    documentos,
                    n);
            }

            // ========================================================
            // DEFINICIÓN
            // ========================================================

            if (EsConsultaDefinicion(n))
            {
                return BuscarRespuestaDefinicion(
                    documentos,
                    n);
            }

            // ========================================================
            // FECHA
            // ========================================================

            if (EsConsultaFecha(n))
            {
                return BuscarRespuestaFecha(
                    documentos,
                    n);
            }
        
            return null;
        }

        // ============================================================
        // CONTEXTO GENERAL
        // ============================================================

        /// <summary>
        /// Busca el documento más relevante y devuelve solamente
        /// las oraciones más relacionadas con la consulta.
        /// </summary>
        public async Task<(string Titulo, string Contenido)?>
            BuscarContextoGeneralAsync(
                string consulta)
        {
            if (string.IsNullOrWhiteSpace(consulta))
                return null;

            var n =
                NormalizarTexto(consulta);

            if (EsGobernadorActual(n))
                return null;

            var documentos =
                await ObtenerDocumentosAsync();

            if (documentos.Count == 0)
                return null;

            var palabras =
                ObtenerPalabrasRelevantes(n);

            var entidades =
                ObtenerEntidadesConsulta(n);

            bool consultaHistoria =
                ContieneAlguna(
                    n,
                    "historia",
                    "historico",
                    "historica",
                    "origen",
                    "origenes",
                    "fundacion",
                    "fundada");

            bool consultaTurismo =
                ContieneAlguna(
                    n,
                    "turismo",
                    "turistico",
                    "turistica",
                    "turisticos",
                    "turisticas",
                    "visitar",
                    "visitas",
                    "hacer",
                    "actividades");

            bool consultaNaturaleza =
                ContieneAlguna(
                    n,
                    "naturaleza",
                    "natural",
                    "naturales",
                    "esteros",
                    "humedal",
                    "humedales",
                    "parque");

            DocumentoIndexado? mejor = null;
            int mejorScore = 0;

            foreach (var documento in documentos)
            {
                int score = 0;

                // ====================================================
                // TÍTULO
                // ====================================================

                foreach (var palabra in palabras)
                {
                    if (documento.TituloTokens.Contains(palabra))
                    {
                        score += 100;
                    }
                }

                // ====================================================
                // ENTIDADES
                // ====================================================

                foreach (var entidad in entidades)
                {
                    if (ContieneFrase(
                        documento.TituloNorm,
                        entidad))
                    {
                        score += 300;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        entidad))
                    {
                        score += 25;
                    }
                }

                // ====================================================
                // CONTENIDO
                // ====================================================

                foreach (var palabra in palabras)
                {
                    if (documento.ContenidoTokens.Contains(palabra))
                    {
                        score += 3;
                    }
                }

                // ====================================================
                // HISTORIA
                // ====================================================

                if (consultaHistoria)
                {
                    if (ContieneFrase(
                        documento.TituloNorm,
                        "historia de la provincia de corrientes"))
                    {
                        score += 1000;
                    }
                    else if (ContieneFrase(
                        documento.TituloNorm,
                        "historia"))
                    {
                        score += 600;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "historia"))
                    {
                        score += 50;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "fundada"))
                    {
                        score += 20;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "fundacion"))
                    {
                        score += 20;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "origen"))
                    {
                        score += 20;
                    }
                }

                // ====================================================
                // TURISMO
                // ====================================================

                if (consultaTurismo)
                {
                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "turismo"))
                    {
                        score += 100;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "turistico"))
                    {
                        score += 100;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "atraccion"))
                    {
                        score += 80;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "actividades"))
                    {
                        score += 80;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "visitar"))
                    {
                        score += 60;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "playas"))
                    {
                        score += 40;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "monumentos"))
                    {
                        score += 40;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "museos"))
                    {
                        score += 40;
                    }
                }

                // ====================================================
                // NATURALEZA
                // ====================================================

                if (consultaNaturaleza)
                {
                    if (ContieneFrase(
                        documento.TituloNorm,
                        "esteros del ibera"))
                    {
                        score += 600;
                    }

                    if (ContieneFrase(
                        documento.TituloNorm,
                        "parque nacional ibera"))
                    {
                        score += 500;
                    }

                    if (ContieneFrase(
                        documento.TituloNorm,
                        "parque nacional mburucuya"))
                    {
                        score += 500;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "humedal"))
                    {
                        score += 100;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "humedales"))
                    {
                        score += 100;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "fauna"))
                    {
                        score += 40;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "flora"))
                    {
                        score += 40;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "biodiversidad"))
                    {
                        score += 60;
                    }
                }

                // ====================================================
                // EVITAR DOCUMENTOS DEMOGRÁFICOS
                // ====================================================

                if (!EsConsultaPoblacion(n))
                {
                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "censo 2022"))
                    {
                        score -= 100;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "habitantes"))
                    {
                        score -= 40;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "poblacion"))
                    {
                        score -= 30;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "439270"))
                    {
                        score -= 100;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        "358223"))
                    {
                        score -= 100;
                    }
                }

                // ====================================================
                // PRIORIZAR ENTIDAD EN TÍTULO
                // ====================================================

                if (entidades.Count > 0)
                {
                    bool tieneEntidadEnTitulo =
                        entidades.Any(entidad =>
                            ContieneFrase(
                                documento.TituloNorm,
                                entidad));

                    if (!tieneEntidadEnTitulo)
                    {
                        score -= 60;
                    }
                }

                // ====================================================
                // SELECCIÓN
                // ====================================================

                if (score > mejorScore)
                {
                    mejorScore = score;
                    mejor = documento;
                }
            }

            if (mejor == null ||
                mejorScore < PuntajeMinimoGeneral)
            {
                return null;
            }

            var contexto =
                ConstruirContextoRelevante(
                    mejor,
                    n,
                    palabras,
                    entidades);

            if (string.IsNullOrWhiteSpace(contexto))
                return null;

            Console.WriteLine(
                $"[GENERAL-DOC] {mejor.Titulo} | SCORE {mejorScore}");

            Console.WriteLine(
                $"[GENERAL-CONTEXTO] {contexto}");

            return (
                mejor.Titulo,
                contexto);
        }

        // ============================================================
        // CONSTRUCCIÓN DE CONTEXTO RELEVANTE
        // ============================================================

        private static string ConstruirContextoRelevante(
            DocumentoIndexado documento,
            string consulta,
            IReadOnlyList<string> palabras,
            IReadOnlyList<string> entidades)
        {
            var oraciones =
                SepararOraciones(documento.Contenido)
                    .Where(o => !string.IsNullOrWhiteSpace(o))
                    .Select(o => o.Trim())
                    .Where(o => o.Length >= 55)
                    .ToList();

            if (oraciones.Count == 0)
                return PrimeraOracion(documento.Contenido);

            bool consultaHistoria =
                ContieneAlguna(
                    consulta,
                    "historia",
                    "historico",
                    "historica",
                    "origen",
                    "origenes",
                    "fundacion",
                    "fundada");

            bool consultaTurismo =
                ContieneAlguna(
                    consulta,
                    "turismo",
                    "turistico",
                    "turistica",
                    "turisticos",
                    "turisticas",
                    "visitar",
                    "visitas",
                    "hacer",
                    "actividades");

            bool consultaNaturaleza =
                ContieneAlguna(
                    consulta,
                    "naturaleza",
                    "natural",
                    "naturales",
                    "esteros",
                    "humedal",
                    "humedales",
                    "parque");

            bool consultaCapital =
                EsConsultaCapital(consulta);

            var puntuadas =
                new List<OracionPuntuada>();

            foreach (var oracion in oraciones)
            {
                var norm =
                    NormalizarTexto(oracion);

                int score = 0;

                // ====================================================
                // ENTIDADES
                // ====================================================

                foreach (var entidad in entidades)
                {
                    if (ContieneFrase(norm, entidad))
                    {
                        score += 80;

                        if (norm.StartsWith(
                            entidad + " ",
                            StringComparison.Ordinal))
                        {
                            score += 40;
                        }
                    }
                }

                // ====================================================
                // PALABRAS RELEVANTES
                // ====================================================

                foreach (var palabra in palabras)
                {
                    if (ContieneFrase(norm, palabra))
                        score += 12;
                }

                // ====================================================
                // CAPITAL
                // ====================================================

                if (consultaCapital)
                {
                    if (ContieneFrase(
                        norm,
                        "capital de la provincia"))
                    {
                        score += 700;
                    }

                    if (ContieneFrase(
                        norm,
                        "capital provincial"))
                    {
                        score += 600;
                    }

                    if (ContieneFrase(
                        norm,
                        "capital de corrientes"))
                    {
                        score += 600;
                    }

                    if (ContieneFrase(
                        norm,
                        "ciudad capital"))
                    {
                        score += 500;
                    }

                    // "ciudad de Corrientes" por sí solo NO determina
                    // que la oración responda cuál es la capital.
                }

                // ====================================================
                // HISTORIA
                // ====================================================

                if (consultaHistoria)
                {
                    if (ContieneFrase(norm, "historia"))
                        score += 140;

                    if (ContieneFrase(norm, "historico"))
                        score += 100;

                    if (ContieneFrase(norm, "fundada"))
                        score += 130;

                    if (ContieneFrase(norm, "fundado"))
                        score += 130;

                    if (ContieneFrase(norm, "fundacion"))
                        score += 130;

                    if (ContieneFrase(norm, "origen"))
                        score += 100;

                    if (ContieneFrase(norm, "origenes"))
                        score += 100;

                    if (ContieneFrase(norm, "siglo"))
                        score += 80;

                    if (ContieneFrase(norm, "colonial"))
                        score += 80;

                    if (ContieneFrase(norm, "independencia"))
                        score += 80;

                    if (ContieneFrase(norm, "revolucion"))
                        score += 80;

                    if (ContieneFrase(
                        norm,
                        "capital de la provincia"))
                    {
                        score -= 100;
                    }

                    if (ContieneFrase(norm, "habitantes"))
                        score -= 100;

                    if (ContieneFrase(norm, "censo"))
                        score -= 120;
                }

                // ====================================================
                // TURISMO
                // ====================================================

                if (consultaTurismo)
                {
                    if (ContieneFrase(norm, "turismo"))
                        score += 120;

                    if (ContieneFrase(norm, "turistico"))
                        score += 120;

                    if (ContieneFrase(norm, "turistica"))
                        score += 120;

                    if (ContieneFrase(norm, "actividades"))
                        score += 120;

                    if (ContieneFrase(norm, "visitar"))
                        score += 100;

                    if (ContieneFrase(norm, "visitas"))
                        score += 100;

                    if (ContieneFrase(norm, "atraccion"))
                        score += 100;

                    if (ContieneFrase(norm, "paseos"))
                        score += 80;

                    if (ContieneFrase(norm, "playas"))
                        score += 80;

                    if (ContieneFrase(norm, "monumentos"))
                        score += 80;

                    if (ContieneFrase(norm, "museos"))
                        score += 80;

                    if (ContieneFrase(norm, "cultura"))
                        score += 60;

                    if (ContieneFrase(norm, "habitantes"))
                        score -= 100;

                    if (ContieneFrase(norm, "censo"))
                        score -= 120;
                }

                // ====================================================
                // NATURALEZA
                // ====================================================

                if (consultaNaturaleza)
                {
                    if (ContieneFrase(norm, "humedal"))
                        score += 140;

                    if (ContieneFrase(norm, "humedales"))
                        score += 140;

                    if (ContieneFrase(norm, "naturaleza"))
                        score += 120;

                    if (ContieneFrase(norm, "fauna"))
                        score += 100;

                    if (ContieneFrase(norm, "flora"))
                        score += 100;

                    if (ContieneFrase(norm, "biodiversidad"))
                        score += 100;

                    if (ContieneFrase(norm, "parque"))
                        score += 90;

                    if (ContieneFrase(norm, "laguna"))
                        score += 80;

                    if (ContieneFrase(norm, "esteros"))
                        score += 120;

                    if (ContieneFrase(norm, "turismo"))
                        score += 70;

                    if (ContieneFrase(norm, "turistico"))
                        score += 70;
                }

                // ====================================================
                // DESCARTAR DEMOGRAFÍA IRRELEVANTE
                // ====================================================

                if (!EsConsultaPoblacion(consulta))
                {
                    if (ContieneFrase(norm, "censo 2022"))
                        score -= 180;

                    if (ContieneFrase(norm, "censo"))
                        score -= 120;

                    if (ContieneFrase(norm, "habitantes"))
                        score -= 100;

                    if (ContieneFrase(norm, "poblacion"))
                        score -= 80;

                    if (ContieneFrase(norm, "439270"))
                        score -= 180;

                    if (ContieneFrase(norm, "358223"))
                        score -= 180;
                }

                // ====================================================
                // DESCARTAR FRAGMENTOS DE MALA CALIDAD
                // ====================================================

                if (norm.StartsWith(
                    "los origenes y",
                    StringComparison.Ordinal))
                {
                    score -= 180;
                }

                if (norm.StartsWith(
                    "y constituye",
                    StringComparison.Ordinal))
                {
                    score -= 150;
                }

                if (norm.Contains("..."))
                    score -= 30;

                if (oracion.Length < 80)
                    score -= 10;

                puntuadas.Add(
                    new OracionPuntuada(
                        oracion,
                        score));
            }

            var seleccionadas =
    puntuadas
        .Where(x => x.Score > 0)
        .OrderByDescending(x => x.Score)
        .Take(MaxOracionesContextoGeneral)
        .ToList();

if (consultaHistoria)
{
    var historicas =
        puntuadas
            .Where(x =>
                x.Score > 0 &&
                (
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "historia") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "historico") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "historica") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "fundada") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "fundado") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "fundacion") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "origen") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "origenes") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "colonial") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "independencia") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "revolucion")
                ))
            .OrderByDescending(x => x.Score)
            .Take(MaxOracionesContextoGeneral)
            .ToList();

    if (historicas.Count > 0)
        seleccionadas = historicas;
}

if (consultaTurismo)
{
    var turisticas =
        puntuadas
            .Where(x =>
                x.Score > 0 &&
                (
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "turismo") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "turistico") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "turistica") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "actividades") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "visitar") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "visitas") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "atraccion") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "paseos") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "playas") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "monumentos") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "museos") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "cultura")
                ))
            .OrderByDescending(x => x.Score)
            .Take(MaxOracionesContextoGeneral)
            .ToList();

    if (turisticas.Count > 0)
        seleccionadas = turisticas;
}

if (consultaNaturaleza)
{
    var naturales =
        puntuadas
            .Where(x =>
                x.Score > 0 &&
                (
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "humedal") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "humedales") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "naturaleza") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "fauna") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "flora") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "biodiversidad") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "parque") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "laguna") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "esteros") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "turismo") ||
                    ContieneFrase(
                        NormalizarTexto(x.Texto),
                        "turistico")
                ))
            .OrderByDescending(x => x.Score)
            .Take(MaxOracionesContextoGeneral)
            .ToList();

    if (naturales.Count > 0)
        seleccionadas = naturales;
}

            if (seleccionadas.Count == 0)
                return PrimeraOracion(documento.Contenido);

            var resultado =
                seleccionadas
                    .Select(x => x.Texto)
                    .ToList();

            // Mantener el orden original del documento.
            resultado =
                resultado
                    .OrderBy(oracion =>
                    {
                        int indice =
                            documento.Contenido.IndexOf(
                                oracion,
                                StringComparison.OrdinalIgnoreCase);

                        return indice < 0
                            ? int.MaxValue
                            : indice;
                    })
                    .ToList();

            return string.Join(" ", resultado);
        }

        // ============================================================
        // CAPITAL
        // ============================================================

        private static bool EsConsultaCapital(
            string n) =>
            ContieneAlguna(
                n,
                "cual es la capital",
                "cual es la capital de corrientes",
                "capital de corrientes",
                "capital provincial",
                "capital de la provincia de corrientes");

        private static string BuscarRespuestaCapital(
            IReadOnlyList<DocumentoIndexado> documentos)
        {
            bool hayCiudad =
                documentos.Any(d =>
                    ContieneFrase(
                        d.TituloNorm,
                        TituloCiudad));

            if (!hayCiudad)
            {
                return
                    "La capital de la provincia de Corrientes es la ciudad de Corrientes.";
            }

            // La respuesta es un dato estructural y no depende
            // de encontrar una oración incidental dentro de Wikipedia.
            return
                "La capital de la provincia de Corrientes es la ciudad de Corrientes.";
        }

        // ============================================================
        // DATOS DUROS
        // ============================================================

        private async Task<string?> BuscarDatoDuroInternoAsync(
            string n)
        {
            if (n.Length == 0)
                return null;

            var datos =
                await ObtenerDatosDurosAsync();

            DatoDuroIndexado? mejor = null;
            int mejorPeso = 0;

            foreach (var dato in datos)
            {
                if (dato.Palabras.Length == 0)
                    continue;

                int peso = 0;

                if (dato.Palabras.All(
                    p => ContieneFrase(n, p)))
                {
                    peso =
                        100 +
                        dato.Palabras.Length;
                }
                else if (
                    dato.ValorNorm.Length >=
                    LongitudMinimaValorDatoDuro &&
                    ContieneFrase(
                        n,
                        dato.ValorNorm))
                {
                    peso = 1;
                }

                if (peso > mejorPeso)
                {
                    mejorPeso = peso;
                    mejor = dato;
                }
            }

            return mejor == null
                ? null
                : $"{mejor.Clave}: {mejor.Valor}";
        }

        // ============================================================
        // LUGARES
        // ============================================================

        private async Task<string?> BuscarLugaresInternoAsync(
            string n)
        {
            if (n.Length == 0)
                return null;

            var lugares =
                await ObtenerLugaresAsync();

            LugarIndexado? mejor = null;
            int mejorPeso = 0;

            foreach (var lugar in lugares)
            {
                if (lugar.NombreNorm.Length == 0)
                    continue;

                bool coincideNombre =
                    ContieneFrase(
                        n,
                        lugar.NombreNorm);

                bool coincideLocalidad =
                    lugar.LocalidadNorm.Length > 0 &&
                    ContieneFrase(
                        n,
                        lugar.LocalidadNorm);

                bool coincideCategoria =
                    lugar.CategoriaNorm.Length > 0 &&
                    ContieneFrase(
                        n,
                        lugar.CategoriaNorm);

                int peso = 0;

                if (coincideNombre)
                {
                    peso =
                        1000 +
                        lugar.NombreNorm.Length;
                }
                else if (
                    coincideLocalidad &&
                    coincideCategoria)
                {
                    peso = 1;
                }

                if (peso > mejorPeso)
                {
                    mejorPeso = peso;
                    mejor = lugar;
                }
            }

            return mejor == null
                ? null
                : $"{mejor.Nombre}: {mejor.Descripcion}";
        }

        // ============================================================
        // POBLACIÓN
        // ============================================================

        private static string? BuscarRespuestaPoblacion(
            IReadOnlyList<DocumentoIndexado> documentos,
            string n)
        {
            bool mencionaProvincia =
                ContieneFrase(
                    n,
                    "provincia");

            bool mencionaCorrientes =
                ContieneFrase(
                    n,
                    "corrientes");

            bool mencionaCiudad =
                ContieneFrase(
                    n,
                    "capital") ||
                (
                    ContieneFrase(
                        n,
                        "ciudad") &&
                    mencionaCorrientes
                );

            var localidades =
                Localidades
                    .Where(l =>
                        ContieneFrase(
                            n,
                            l))
                    .ToList();

            IEnumerable<DocumentoIndexado> candidatos;

            if (localidades.Count > 0)
            {
                candidatos =
                    documentos.Where(d =>
                        localidades.Any(l =>
                            ContieneFrase(
                                d.TituloNorm,
                                l)));
            }
            else if (mencionaCiudad)
            {
                candidatos =
                    documentos.Where(d =>
                        ContieneFrase(
                            d.TituloNorm,
                            TituloCiudad));
            }
            else if (mencionaProvincia)
            {
                candidatos =
                    documentos.Where(d =>
                        ContieneFrase(
                            d.TituloNorm,
                            TituloProvincia));
            }
            else if (mencionaCorrientes)
            {
                candidatos =
                    documentos.Where(d =>
                        ContieneFrase(
                            d.TituloNorm,
                            TituloCiudad) ||
                        ContieneFrase(
                            d.TituloNorm,
                            TituloProvincia));
            }
            else
            {
                return null;
            }

            string? mejorRespuesta = null;
            int mejorScore = 0;

            foreach (var documento in candidatos)
            {
                var (
                    oracion,
                    score) =
                    BuscarOracionPoblacion(
                        documento.Contenido);

                if (oracion != null &&
                    score > mejorScore)
                {
                    mejorScore = score;

                    mejorRespuesta =
                        $"{documento.Titulo}: {oracion}";
                }
            }

            return mejorRespuesta;
        }

        private static (
            string? Oracion,
            int Score
        ) BuscarOracionPoblacion(
            string contenido)
        {
            string? mejor = null;
            int mejorScore = 0;

            foreach (var oracion in
                SepararOraciones(contenido))
            {
                var norm =
                    NormalizarTexto(oracion);

                bool habitantes =
                    ContieneFrase(
                        norm,
                        "habitantes");

                bool poblacion =
                    ContieneFrase(
                        norm,
                        "poblacion");

                bool censo =
                    ContieneFrase(
                        norm,
                        "censo");

                bool indec =
                    ContieneFrase(
                        norm,
                        "indec");

                if (!habitantes &&
                    !poblacion &&
                    !censo &&
                    !indec)
                {
                    continue;
                }

                int score = 0;

                if (habitantes)
                    score += 50;

                if (poblacion)
                    score += 30;

                if (censo)
                    score += 20;

                if (indec)
                    score += 20;

                if (ContieneFecha(norm))
                    score += 10;

                if (RegexDigito.IsMatch(norm))
                    score += 25;

                if (score > mejorScore)
                {
                    mejorScore = score;
                    mejor = oracion;
                }
            }

            return (
                mejor,
                mejorScore);
        }

        // ============================================================
        // LISTAS DE LOCALIDADES
        // ============================================================

        private static string? ConstruirRespuestaListaLocalidades(
            IReadOnlyList<DocumentoIndexado> documentos,
            string etiqueta,
            string cierre)
        {
            var nombres =
                documentos
                    .Where(d =>
                        EsDocumentoLocalidad(
                            d.TituloNorm))
                    .Select(d =>
                        d.Titulo.Trim())
                    .Where(t =>
                        t.Length > 0)
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (nombres.Count == 0)
                return null;

            return
                "En el corpus actual, CorrientesIA tiene información sobre " +
                etiqueta +
                " como " +
                string.Join(
                    ", ",
                    nombres) +
                cierre;
        }

        // ============================================================
        // LISTA TURÍSTICA
        // ============================================================

        private static string? ConstruirRespuestaListaTuristica(
            IReadOnlyList<DocumentoIndexado> documentos,
            string n)
        {
            bool pideNaturales =
                ContieneAlguna(
                    n,
                    "natural",
                    "naturales",
                    "naturaleza",
                    "atractivos naturales");

            if (pideNaturales)
            {
                var naturales =
                    documentos
                        .Where(d =>
                            EsDocumentoTuristicoNatural(
                                d.TituloNorm))
                        .Select(d =>
                            d.Titulo.Trim())
                        .Where(t =>
                            t.Length > 0)
                        .Distinct(
                            StringComparer.OrdinalIgnoreCase)
                        .ToList();

                if (naturales.Count == 0)
                    return null;

                return
                    "En el corpus actual, CorrientesIA tiene información sobre " +
                    "estos lugares turísticos naturales: " +
                    string.Join(
                        ", ",
                        naturales) +
                    ".";
            }

            var lugares =
                documentos
                    .Where(d =>
                        EsDocumentoTuristico(
                            d.TituloNorm))
                    .Select(d =>
                        d.Titulo.Trim())
                    .Where(t =>
                        t.Length > 0)
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (lugares.Count == 0)
                return null;

            return
                "En el corpus actual, CorrientesIA tiene información sobre " +
                "estos lugares y atractivos turísticos: " +
                string.Join(
                    ", ",
                    lugares) +
                ".";
        }

        private static bool EsDocumentoLocalidad(
            string tituloNorm) =>
            Localidades.Any(l =>
                ContieneFrase(
                    tituloNorm,
                    l));

        private static bool EsDocumentoTuristico(
            string tituloNorm) =>
            DocumentosTuristicos.Any(t =>
                ContieneFrase(
                    tituloNorm,
                    t));

        private static bool EsDocumentoTuristicoNatural(
            string tituloNorm) =>
            DocumentosTuristicosNaturales.Any(t =>
                ContieneFrase(
                    tituloNorm,
                    t));

        // ============================================================
        // CIUDAD VS PROVINCIA
        // ============================================================

        private static string? ConstruirRespuestaCiudadVsProvincia(
            IReadOnlyList<DocumentoIndexado> documentos)
        {
            bool hayCiudad =
                documentos.Any(d =>
                    ContieneFrase(
                        d.TituloNorm,
                        TituloCiudad));

            bool hayProvincia =
                documentos.Any(d =>
                    ContieneFrase(
                        d.TituloNorm,
                        TituloProvincia));

            if (!hayCiudad || !hayProvincia)
                return null;

            return
                "Corrientes Capital es la ciudad que funciona como capital " +
                "de la provincia de Corrientes. La ciudad es el principal " +
                "centro administrativo, social y económico de la provincia, " +
                "mientras que la Provincia de Corrientes es la entidad " +
                "territorial provincial a la que pertenece esa ciudad.";
        }

        // ============================================================
        // UBICACIÓN
        // ============================================================

        private static string? BuscarRespuestaUbicacion(
            IReadOnlyList<DocumentoIndexado> documentos,
            string n)
        {
            var palabras =
                ObtenerPalabrasRelevantes(n);

            if (palabras.Count == 0)
                return null;

            DocumentoIndexado? mejor = null;
            int mejorScore = 0;

            foreach (var documento in documentos)
            {
                int score = 0;

                foreach (var palabra in palabras)
                {
                    if (documento.TituloTokens.Contains(palabra))
                        score += 200;
                }

                foreach (var palabra in palabras)
                {
                    if (documento.ContenidoTokens.Contains(palabra))
                        score += 10;
                }

                if (ContieneFrase(
                    documento.ContenidoNorm,
                    "ubicado"))
                {
                    score += 30;
                }

                if (ContieneFrase(
                    documento.ContenidoNorm,
                    "ubicada"))
                {
                    score += 30;
                }

                if (ContieneFrase(
                    documento.ContenidoNorm,
                    "se encuentra"))
                {
                    score += 30;
                }

                if (ContieneFrase(
                    documento.ContenidoNorm,
                    "provincia de corrientes"))
                {
                    score += 20;
                }

                if (score > mejorScore)
                {
                    mejorScore = score;
                    mejor = documento;
                }
            }

            if (mejor == null ||
                mejorScore == 0)
            {
                return null;
            }

            var oracion =
                BuscarOracionUbicacion(
                    mejor.Contenido);

            Console.WriteLine(
                $"[UBICACION-DOC] {mejor.Titulo} | SCORE {mejorScore}");

            if (string.IsNullOrWhiteSpace(oracion))
            {
                oracion =
                    PrimeraOracion(
                        mejor.Contenido);
            }

            return string.IsNullOrWhiteSpace(oracion)
                ? null
                : $"{mejor.Titulo}: {oracion}";
        }

        private static string? BuscarOracionUbicacion(
            string contenido)
        {
            var oraciones =
                SepararOraciones(
                    contenido);

            if (oraciones.Count == 0)
                return null;

            string? mejor = null;
            int mejorPuntaje = 0;

            foreach (var oracion in oraciones)
            {
                var norm =
                    NormalizarTexto(oracion);

                int puntaje = 0;

                if (ContieneFrase(
                    norm,
                    "es una ciudad argentina"))
                {
                    puntaje += 250;
                }

                if (ContieneFrase(
                    norm,
                    "es una ciudad"))
                {
                    puntaje += 220;
                }

                if (ContieneFrase(
                    norm,
                    "es un municipio"))
                {
                    puntaje += 220;
                }

                if (ContieneFrase(
                    norm,
                    "es una localidad"))
                {
                    puntaje += 220;
                }

                if (ContieneFrase(
                    norm,
                    "esta ubicada"))
                {
                    puntaje += 200;
                }

                if (ContieneFrase(
                    norm,
                    "esta ubicado"))
                {
                    puntaje += 200;
                }

                if (ContieneFrase(
                    norm,
                    "se encuentra"))
                {
                    puntaje += 180;
                }

                if (ContieneFrase(
                    norm,
                    "se ubica"))
                {
                    puntaje += 180;
                }

                if (ContieneFrase(
                    norm,
                    "situada en"))
                {
                    puntaje += 180;
                }

                if (ContieneFrase(
                    norm,
                    "situado en"))
                {
                    puntaje += 180;
                }

                if (ContieneFrase(
                    norm,
                    "ubicada en"))
                {
                    puntaje += 180;
                }

                if (ContieneFrase(
                    norm,
                    "ubicado en"))
                {
                    puntaje += 180;
                }

                if (ContieneFrase(
                    norm,
                    "provincia de corrientes"))
                {
                    puntaje += 120;
                }

                if (ContieneFrase(
                    norm,
                    "en la provincia"))
                {
                    puntaje += 80;
                }

                if (ContieneFrase(
                    norm,
                    "en el departamento"))
                {
                    puntaje += 60;
                }

                if (ContieneFrase(
                    norm,
                    "argentina"))
                {
                    puntaje += 30;
                }

                if (ContieneFrase(
                    norm,
                    "nordeste"))
                {
                    puntaje += 30;
                }

                if (ContieneFrase(
                    norm,
                    "noreste"))
                {
                    puntaje += 30;
                }

                if (ContieneFrase(
                    norm,
                    "a orillas del"))
                {
                    puntaje += 50;
                }

                if (ContieneFrase(
                    norm,
                    "a orilla del"))
                {
                    puntaje += 50;
                }

                if (ContieneFrase(
                    norm,
                    "al norte de"))
                {
                    puntaje += 40;
                }

                if (ContieneFrase(
                    norm,
                    "al sur de"))
                {
                    puntaje += 40;
                }

                if (ContieneFrase(
                    norm,
                    "cerca de"))
                {
                    puntaje += 30;
                }

                // Penalizaciones.

                if (ContieneFrase(
                    norm,
                    "fue fundada"))
                {
                    puntaje -= 60;
                }

                if (ContieneFrase(
                    norm,
                    "fundada oficialmente"))
                {
                    puntaje -= 60;
                }

                if (ContieneFrase(
                    norm,
                    "nombre"))
                {
                    puntaje -= 50;
                }

                if (ContieneFrase(
                    norm,
                    "general carlos de alvear"))
                {
                    puntaje -= 80;
                }

                if (ContieneFrase(
                    norm,
                    "historia"))
                {
                    puntaje -= 40;
                }

                if (ContieneFrase(
                    norm,
                    "turismo"))
                {
                    puntaje -= 30;
                }

                if (ContieneFrase(
                    norm,
                    "fauna"))
                {
                    puntaje -= 30;
                }

                if (ContieneFrase(
                    norm,
                    "flora"))
                {
                    puntaje -= 30;
                }

                if (puntaje > mejorPuntaje)
                {
                    mejorPuntaje = puntaje;
                    mejor = oracion;
                }
            }

            return mejor;
        }

        // ============================================================
        // DEFINICIÓN
        // ============================================================

        private static string? BuscarRespuestaDefinicion(
            IReadOnlyList<DocumentoIndexado> documentos,
            string n)
        {
            var entidades =
                ObtenerEntidadesConsulta(n);

            if (entidades.Count == 0)
                return null;

            DocumentoIndexado? mejor = null;
            int mejorScore = 0;

            foreach (var documento in documentos)
            {
                int score = 0;

                foreach (var entidad in entidades)
                {
                    if (ContieneFrase(
                        documento.TituloNorm,
                        entidad))
                    {
                        score += 300;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        entidad))
                    {
                        score += 25;
                    }
                }

                if (ContieneFrase(
                    documento.ContenidoNorm,
                    "es un"))
                {
                    score += 15;
                }

                if (ContieneFrase(
                    documento.ContenidoNorm,
                    "es una"))
                {
                    score += 15;
                }

                if (score > mejorScore)
                {
                    mejorScore = score;
                    mejor = documento;
                }
            }

            if (mejor == null ||
                mejorScore == 0)
            {
                return null;
            }

            var oracion =
                BuscarOracionDefinicion(
                    mejor.Contenido,
                    entidades);

            if (string.IsNullOrWhiteSpace(oracion))
                return null;

            return
                $"{mejor.Titulo}: {oracion}";
        }

        private static string? BuscarOracionDefinicion(
            string contenido,
            IReadOnlyList<string> entidades)
        {
            if (string.IsNullOrWhiteSpace(contenido) ||
                entidades.Count == 0)
            {
                return null;
            }

            string? mejor = null;
            int mejorPuntaje = 0;

            foreach (var oracion in
                SepararOraciones(contenido))
            {
                var norm =
                    NormalizarTexto(oracion);

                if (!entidades.Any(entidad =>
                        ContieneFrase(
                            norm,
                            entidad)))
                {
                    continue;
                }

                int puntaje = 0;

                foreach (var entidad in entidades)
                {
                    if (ContieneFrase(
                        norm,
                        entidad))
                    {
                        puntaje += 50;

                        if (norm.StartsWith(
                            entidad + " ",
                            StringComparison.Ordinal))
                        {
                            puntaje += 100;
                        }
                    }
                }

                if (ContieneFrase(
                    norm,
                    "se denomina"))
                {
                    puntaje += 120;
                }

                if (ContieneFrase(
                    norm,
                    "se define como"))
                {
                    puntaje += 120;
                }

                if (ContieneFrase(
                    norm,
                    "es un"))
                {
                    puntaje += 80;
                }

                if (ContieneFrase(
                    norm,
                    "es una"))
                {
                    puntaje += 80;
                }

                if (ContieneFrase(
                    norm,
                    "se trata de"))
                {
                    puntaje += 70;
                }

                if (ContieneFrase(
                    norm,
                    "consiste en"))
                {
                    puntaje += 70;
                }

                if (ContieneFrase(
                    norm,
                    "yaguarete"))
                {
                    puntaje -= 40;
                }

                if (ContieneFrase(
                    norm,
                    "fauna"))
                {
                    puntaje -= 20;
                }

                if (ContieneFrase(
                    norm,
                    "flora"))
                {
                    puntaje -= 20;
                }

                if (ContieneFrase(
                    norm,
                    "turismo"))
                {
                    puntaje -= 20;
                }

                if (ContieneFrase(
                    norm,
                    "visitante"))
                {
                    puntaje -= 20;
                }

                if (puntaje > mejorPuntaje)
                {
                    mejorPuntaje = puntaje;
                    mejor = oracion;
                }
            }

            return mejor;
        }

        // ============================================================
        // FECHA
        // ============================================================

        private static string? BuscarRespuestaFecha(
    IReadOnlyList<DocumentoIndexado> documentos,
    string n)
{
    var entidades =
        ObtenerEntidadesConsulta(n);

    if (entidades.Count == 0)
        return null;

    var mejor =
        MejorDocumentoPorEntidades(
            documentos,
            entidades,
            pesoContenido: 20,
            bonus: d =>
                ContieneFecha(d.ContenidoNorm)
                    ? 20
                    : 0);

    if (mejor == null)
        return null;

    bool consultaFundacion =
        EsConsultaFundacion(n);

    bool consultaPrimeraEdicion =
        ContieneAlguna(
            n,
            "primera fiesta",
            "primera edicion",
            "primera vez",
            "primer fiesta",
            "primer edicion");

    var oraciones =
        SepararOraciones(mejor.Contenido);

    // ============================================================
    // CONSULTA DE PRIMERA EDICIÓN / PRIMERA FIESTA
    // ============================================================

    if (consultaPrimeraEdicion)
    {
        var mejorOracionPrimera =
            oraciones
                .Where(ContieneFecha)
                .Select(oracion => new
                {
                    Oracion = oracion,
                    Normalizada =
                        NormalizarTexto(oracion)
                })
                .Select(x => new
                {
                    x.Oracion,

                    Puntaje =
                        (ContieneFrase(
                            x.Normalizada,
                            "primera fiesta nacional del chamame")
                                ? 180
                                : 0)
                        +
                        (ContieneFrase(
                            x.Normalizada,
                            "primera fiesta")
                                ? 120
                                : 0)
                        +
                        (ContieneFrase(
                            x.Normalizada,
                            "primera edicion")
                                ? 160
                                : 0)
                        +
                        (ContieneFrase(
                            x.Normalizada,
                            "primera vez")
                                ? 140
                                : 0)
                        +
                        (ContieneFrase(
                            x.Normalizada,
                            "se realizo por primera vez")
                                ? 180
                                : 0)
                        +
                        (ContieneFrase(
                            x.Normalizada,
                            "tuvo lugar")
                                ? 80
                                : 0)
                        +
                        (ContieneFrase(
                            x.Normalizada,
                            "se realizo")
                                ? 70
                                : 0)
                        +
                        (ContieneFrase(
                            x.Normalizada,
                            "fiesta nacional del chamame")
                                ? 80
                                : 0)
                        +
                        (x.Normalizada.Contains(
                            NormalizarTexto(
                                entidades[0]))
                                ? 60
                                : 0)
                })
                .Where(x => x.Puntaje >= 100)
                .OrderByDescending(
                    x => x.Puntaje)
                .FirstOrDefault();

        if (mejorOracionPrimera != null)
        {
            return
                $"{mejor.Titulo}: " +
                $"{mejorOracionPrimera.Oracion}";
        }

        // El corpus no contiene una evidencia
        // suficiente sobre la primera edición.
        return null;
    }

    // ============================================================
    // CONSULTA DE FUNDACIÓN / CREACIÓN / ORIGEN
    // ============================================================

    if (consultaFundacion)
    {
        var mejorOracion =
            oraciones
                .Where(ContieneFecha)
                .Select(oracion => new
                {
                    Oracion = oracion,
                    Normalizada =
                        NormalizarTexto(oracion)
                })
                .Select(x => new
                {
                    x.Oracion,

                    Puntaje =
                        (ContieneFrase(
                            x.Normalizada,
                            "se fundo")
                                ? 120
                                : 0)
                        +
                        (ContieneFrase(
                            x.Normalizada,
                            "fue fundada")
                                ? 120
                                : 0)
                        +
                        (ContieneFrase(
                            x.Normalizada,
                            "fue fundado")
                                ? 120
                                : 0)
                        +
                        (ContieneFrase(
                            x.Normalizada,
                            "fundada oficialmente")
                                ? 110
                                : 0)
                        +
                        (ContieneFrase(
                            x.Normalizada,
                            "fundado oficialmente")
                                ? 110
                                : 0)
                        +
                        (ContieneFrase(
                            x.Normalizada,
                            "fundacion")
                                ? 100
                                : 0)
                        +
                        (ContieneFrase(
                            x.Normalizada,
                            "origen")
                                ? 70
                                : 0)
                        +
                        (ContieneFrase(
                            x.Normalizada,
                            "se creo")
                                ? 90
                                : 0)
                        +
                        (ContieneFrase(
                            x.Normalizada,
                            "fue creada")
                                ? 90
                                : 0)
                        +
                        (ContieneFrase(
                            x.Normalizada,
                            "fue creado")
                                ? 90
                                : 0)
                        +
                        (x.Normalizada.Contains(
                            NormalizarTexto(
                                entidades[0]))
                                ? 60
                                : 0)
                })
                .OrderByDescending(
                    x => x.Puntaje)
                .FirstOrDefault();

        if (mejorOracion != null &&
            mejorOracion.Puntaje >= 70)
        {
            return
                $"{mejor.Titulo}: " +
                $"{mejorOracion.Oracion}";
        }

        // La consulta pide específicamente
        // fundación, creación u origen.
        // Si el corpus no contiene esa información,
        // NO devolver una fecha cualquiera.
        return null;
    }

    // ============================================================
    // FECHA GENERAL
    // ============================================================

    foreach (var oracion in oraciones)
    {
        if (ContieneFecha(oracion))
        {
            return
                $"{mejor.Titulo}: {oracion}";
        }
    }

    return null;
}

        // ============================================================
        // RANKING
        // ============================================================

        private static DocumentoIndexado?
            MejorDocumentoPorEntidades(
                IReadOnlyList<DocumentoIndexado> documentos,
                IReadOnlyList<string> entidades,
                int pesoContenido,
                Func<DocumentoIndexado, int> bonus)
        {
            DocumentoIndexado? mejor = null;
            int mejorScore = 0;

            foreach (var documento in documentos)
            {
                int score = 0;

                foreach (var entidad in entidades)
                {
                    if (ContieneFrase(
                        documento.TituloNorm,
                        entidad))
                    {
                        score += 100;
                    }

                    if (ContieneFrase(
                        documento.ContenidoNorm,
                        entidad))
                    {
                        score += pesoContenido;
                    }
                }

                if (score == 0)
                    continue;

                score += bonus(documento);

                if (score > mejorScore)
                {
                    mejorScore = score;
                    mejor = documento;
                }
            }

            return mejor;
        }

        // ============================================================
        // RESPUESTAS META
        // ============================================================

        private async Task<string?>
            ConstruirRespuestaSobreFaltantesAsync()
        {
            var documentos =
                await ObtenerDocumentosAsync();

            var datosDuros =
                await ObtenerDatosDurosAsync();

            var respuesta =
                new StringBuilder();

            respuesta.Append(
                $"Actualmente CorrientesIA dispone de {documentos.Count} documentos en su corpus.");

            respuesta.Append(
                " La información disponible incluye la ciudad y " +
                "provincia de Corrientes, historia, Esteros del Iberá, " +
                "parques, chamamé, Río Paraná y distintas localidades.");

            if (datosDuros.Count == 0)
            {
                respuesta.Append(
                    " La tabla de datos duros no contiene registros " +
                    "estructurados actualmente.");
            }

            respuesta.Append(
                " Además, las consultas que requieren información " +
                "actualizada, como autoridades vigentes, no deben " +
                "resolverse utilizando documentos históricos del corpus.");

            return respuesta.ToString();
        }

        private async Task<string?>
            ConstruirRespuestaSobreConocimientoAsync()
        {
            var documentos =
                await ObtenerDocumentosAsync();

            var titulos =
                documentos
                    .Select(d =>
                        d.Titulo)
                    .Where(t =>
                        !string.IsNullOrWhiteSpace(t))
                    .ToList();

            if (titulos.Count == 0)
                return null;

            return
                $"Actualmente CorrientesIA tiene {titulos.Count} documentos " +
                $"en su corpus. Entre los temas disponibles se encuentran: " +
                $"{string.Join(", ", titulos)}.";
        }

        // ============================================================
        // DETECCIÓN DE INTENCIONES
        // ============================================================

        private static bool EsGobernadorActual(
            string n) =>
            EsConsultaGobernador(n) &&
            EsConsultaActualidad(n);

        private static bool EsMetaConocimiento(
            string n) =>
            EsConsultaConocimiento(n) &&
            !EsConsultaInformacionLugar(n);

        private static bool EsConsultaPoblacion(
            string n) =>
            ContieneAlguna(
                n,
                "poblacion",
                "habitantes",
                "censo");

        private static bool EsConsultaGobernador(
            string n) =>
            ContieneAlguna(
                n,
                "gobernador",
                "gobernadora");

        private static bool EsConsultaMunicipios(
            string n) =>
            ContieneAlguna(
                n,
                "municipio",
                "municipios");

        private static bool EsConsultaLocalidades(
            string n) =>
            ContieneAlguna(
                n,
                "localidad",
                "localidades");

        private static bool EsConsultaTurismo(
            string n) =>
            ContieneAlguna(
                n,
                "turismo",
                "turistico",
                "turistica",
                "turisticos",
                "turisticas",
                "lugares turisticos",
                "atractivos turisticos",
                "atracciones turisticas");

        private static bool EsConsultaActualidad(
            string n) =>
            ContieneAlguna(
                n,
                "actual",
                "actualmente",
                "actualidad",
                "actuales",
                "hoy",
                "ahora",
                "vigente",
                "vigentes",
                "gobierna",
                "quien ocupa",
                "quien es el gobernador",
                "quien es el actual",
                "quien es la actual");

        private static bool EsConsultaFaltante(
            string n) =>
            ContieneAlguna(
                n,
                "que informacion no tiene",
                "que informacion le falta",
                "que datos no tiene",
                "que no tiene",
                "que no sabe",
                "que informacion falta",
                "que le falta",
                "que cosas no sabe");

        private static bool EsConsultaConocimiento(
            string n) =>
            ContieneAlguna(
                n,
                "que informacion tiene corrientesia",
                "que sabe corrientesia",
                "que conoce corrientesia",
                "que informacion conoce corrientesia",
                "que tiene corrientesia",
                "sobre que tiene informacion");

        private static bool EsConsultaUbicacion(
            string n) =>
            ContieneAlguna(
                n,
                "donde",
                "ubicado",
                "ubicada",
                "ubicacion",
                "localizado",
                "localizada");

        private static bool EsConsultaInformacionLugar(
            string n) =>
            ContieneAlguna(
                n,
                "informacion sobre",
                "que informacion tiene corrientesia sobre",
                "que informacion conoce corrientesia sobre",
                "que sabe corrientesia sobre");

        private static bool EsConsultaDefinicion(
            string n) =>
            ContieneAlguna(
                n,
                "significa",
                "define",
                "definicion",
                "que es");

        private static bool EsConsultaFecha(
            string n) =>
            ContieneAlguna(
                n,
                "cuando",
                "fecha",
                "en que ano");

        private static bool EsConsultaFundacion(string n)
        {
            return
                ContieneFrase(n, "cuando se fundo") ||
                ContieneFrase(n, "cuando fue fundado") ||
                ContieneFrase(n, "cuando fue fundada") ||
                ContieneFrase(n, "cuando se fundo oficialmente") ||
                ContieneFrase(n, "fecha de fundacion") ||
                ContieneFrase(n, "ano de fundacion") ||
                ContieneFrase(n, "anio de fundacion") ||
                ContieneFrase(n, "cuando se creo") ||
                ContieneFrase(n, "se creo") ||
                ContieneFrase(n, "cuando fue creado") ||
                ContieneFrase(n, "cuando fue creada") ||
                ContieneFrase(n, "origen de");
             
        }
        private static bool EsConsultaDiferenciaCiudadProvincia(
            string n) =>
            ContieneFrase(
                n,
                "diferencia") &&
            ContieneAlguna(
                n,
                "capital",
                "ciudad") &&
            ContieneFrase(
                n,
                "provincia");

        // ============================================================
        // ENTIDADES
        // ============================================================

        private static List<string>
            ObtenerEntidadesConsulta(
                string n)
        {
            var entidades =
                new List<string>();

            foreach (var entidad in
                EntidadesOrdenadas)
            {
                if (!ContieneFrase(
                    n,
                    entidad))
                {
                    continue;
                }

                if (entidades.Any(e =>
                    ContieneFrase(
                        e,
                        entidad)))
                {
                    continue;
                }

                entidades.Add(entidad);
            }

            if (entidades.Count > 1)
            {
                entidades.Remove("corrientes");
            }

            return entidades;
        }

        private static List<string>
            ObtenerPalabrasRelevantes(
                string n) =>
            n.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries)
                .Where(p =>
                    p.Length >= 3 &&
                    !PalabrasGenericas.Contains(p))
                .Distinct()
                .ToList();

        // ============================================================
        // ORACIONES
        // ============================================================

        private static string PrimeraOracion(
            string? contenido) =>
            string.IsNullOrWhiteSpace(contenido)
                ? string.Empty
                : SepararOraciones(
                    contenido)
                    .FirstOrDefault() ??
                  string.Empty;

        private static List<string>
            SepararOraciones(
                string? contenido)
        {
            if (string.IsNullOrWhiteSpace(contenido))
                return new List<string>();

            return RegexOraciones
                .Split(contenido)
                .Select(x =>
                    x.Trim())
                .Where(x =>
                    x.Length > 0)
                .ToList();
        }

        // ============================================================
        // FECHAS
        // ============================================================

        private static bool ContieneFecha(
            string? texto) =>
            !string.IsNullOrWhiteSpace(texto) &&
            RegexAnio.IsMatch(texto);

        // ============================================================
        // MATCHING
        // ============================================================

        private static bool ContieneAlguna(
            string textoNorm,
            params string[] frases)
        {
            foreach (var frase in frases)
            {
                if (ContieneFrase(
                    textoNorm,
                    frase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContieneFrase(
            string textoNorm,
            string frase)
        {
            if (string.IsNullOrWhiteSpace(textoNorm) ||
                string.IsNullOrWhiteSpace(frase))
            {
                return false;
            }

            int idx = 0;

            while (
                (idx = textoNorm.IndexOf(
                    frase,
                    idx,
                    StringComparison.Ordinal)) >= 0)
            {
                int fin =
                    idx + frase.Length;

                bool inicioOk =
                    idx == 0 ||
                    textoNorm[idx - 1] == ' ';

                bool finOk =
                    fin == textoNorm.Length ||
                    textoNorm[fin] == ' ';

                if (inicioOk && finOk)
                    return true;

                idx++;
            }

            return false;
        }

        // ============================================================
        // NORMALIZACIÓN
        // ============================================================

        private static string NormalizarTexto(
            string? texto)
        {
            if (string.IsNullOrWhiteSpace(texto))
                return string.Empty;

            var descompuesto =
                texto.Normalize(
                    NormalizationForm.FormD);

            var sb =
                new StringBuilder(
                    descompuesto.Length);

            bool ultimoFueEspacio = true;

            foreach (var c in descompuesto)
            {
                if (
                    CharUnicodeInfo.GetUnicodeCategory(c) ==
                    UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(
                        char.ToLowerInvariant(c));

                    ultimoFueEspacio = false;
                }
                else if (!ultimoFueEspacio)
                {
                    sb.Append(' ');
                    ultimoFueEspacio = true;
                }
            }

            if (
                sb.Length > 0 &&
                sb[^1] == ' ')
            {
                sb.Length--;
            }

            return sb.ToString();
        }

        // ============================================================
        // CARGA E INDEXACIÓN
        // ============================================================

        private Task<IReadOnlyList<DocumentoIndexado>>
            ObtenerDocumentosAsync() =>
            ObtenerCacheadoAsync(
                CacheDocumentos,
                async () =>
                {
                    var filas =
                        await _db.CorpusDocumentos
                            .AsNoTracking()
                            .Select(d => new
                            {
                                d.Titulo,
                                d.Contenido
                            })
                            .ToListAsync();

                    return filas
                        .Select(f =>
                            IndexarDocumento(
                                f.Titulo ??
                                string.Empty,
                                f.Contenido ??
                                string.Empty))
                        .ToList();
                });

        private Task<IReadOnlyList<DatoDuroIndexado>>
            ObtenerDatosDurosAsync() =>
            ObtenerCacheadoAsync(
                CacheDatosDuros,
                async () =>
                {
                    var filas =
                        await _db.DatosDuros
                            .AsNoTracking()
                            .Select(d => new
                            {
                                d.Clave,
                                d.Valor
                            })
                            .ToListAsync();

                    return filas
                        .Select(f =>
                        {
                            var clave =
                                f.Clave ??
                                string.Empty;

                            var valor =
                                f.Valor ??
                                string.Empty;

                            var claveNorm =
                                NormalizarTexto(
                                    clave);

                            return new DatoDuroIndexado(
                                clave,
                                valor,
                                claveNorm,
                                claveNorm.Split(
                                    ' ',
                                    StringSplitOptions
                                        .RemoveEmptyEntries),
                                NormalizarTexto(
                                    valor));
                        })
                        .ToList();
                });

        private Task<IReadOnlyList<LugarIndexado>>
            ObtenerLugaresAsync() =>
            ObtenerCacheadoAsync(
                CacheLugares,
                async () =>
                {
                    var filas =
                        await _db.Lugares
                            .AsNoTracking()
                            .Select(l => new
                            {
                                l.Nombre,
                                l.Descripcion,
                                l.Localidad,
                                l.Categoria
                            })
                            .ToListAsync();

                    return filas
                        .Select(f =>
                            new LugarIndexado(
                                f.Nombre ??
                                string.Empty,
                                f.Descripcion ??
                                string.Empty,
                                NormalizarTexto(
                                    f.Nombre),
                                NormalizarTexto(
                                    f.Localidad),
                                NormalizarTexto(
                                    f.Categoria)))
                        .ToList();
                });

        private async Task<IReadOnlyList<T>>
            ObtenerCacheadoAsync<T>(
                string clave,
                Func<Task<List<T>>> cargar)
        {
            if (
                _cache != null &&
                _cache.TryGetValue(
                    clave,
                    out IReadOnlyList<T>? existente) &&
                existente != null)
            {
                return existente;
            }

            var datos =
                await cargar();

            if (
                _cache != null &&
                datos.Count > 0)
            {
                _cache.Set(
                    clave,
                    (IReadOnlyList<T>)datos,
                    CacheTtl);
            }

            return datos;
        }

        private static DocumentoIndexado
            IndexarDocumento(
                string titulo,
                string contenido)
        {
            var tituloNorm =
                NormalizarTexto(
                    titulo);

            var contenidoNorm =
                NormalizarTexto(
                    contenido);

            return new DocumentoIndexado(
                titulo,
                contenido,
                tituloNorm,
                contenidoNorm,
                Tokenizar(tituloNorm),
                Tokenizar(contenidoNorm));
        }

        private static HashSet<string>
            Tokenizar(
                string textoNorm) =>
            new(
                textoNorm.Split(
                    ' ',
                    StringSplitOptions
                        .RemoveEmptyEntries),
                StringComparer.Ordinal);
    }
}