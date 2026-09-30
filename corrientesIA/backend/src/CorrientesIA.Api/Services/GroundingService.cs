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
            "saladas"
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

            var n = NormalizarTexto(consulta);

            if (EsGobernadorActual(n))
                return null;

            if (EsConsultaFaltante(n))
                return await ConstruirRespuestaSobreFaltantesAsync();

            if (EsMetaConocimiento(n))
                return await ConstruirRespuestaSobreConocimientoAsync();

            var datoDuro =
                await BuscarDatoDuroInternoAsync(n);

            if (!string.IsNullOrWhiteSpace(datoDuro))
                return datoDuro;

            if (EsConsultaInformacionLugar(n) &&
    !EsConsultaUbicacion(n))
{
    return await BuscarLugaresInternoAsync(n);
}

            return null;
        }

        public async Task<string?> BuscarEnCorpusAsync(
            string consulta)
        {
            if (string.IsNullOrWhiteSpace(consulta))
                return null;

            var n = NormalizarTexto(consulta);

            if (EsGobernadorActual(n))
                return null;

            if (EsConsultaFaltante(n))
                return await ConstruirRespuestaSobreFaltantesAsync();

            if (EsMetaConocimiento(n))
                return await ConstruirRespuestaSobreConocimientoAsync();

            var documentos =
                await ObtenerDocumentosAsync();

            if (documentos.Count == 0)
                return null;

            // --------------------------------------------------------
            // CIUDAD VS PROVINCIA
            // --------------------------------------------------------

            if (EsConsultaDiferenciaCiudadProvincia(n))
            {
                return ConstruirRespuestaCiudadVsProvincia(
                    documentos);
            }

            // --------------------------------------------------------
            // TURISMO
            // --------------------------------------------------------

            if (EsConsultaTurismo(n))
            {
                return ConstruirRespuestaListaTuristica(
                    documentos,
                    n);
            }

            // --------------------------------------------------------
            // MUNICIPIOS
            // --------------------------------------------------------

            if (EsConsultaMunicipios(n))
            {
                return ConstruirRespuestaListaLocalidades(
                    documentos,
                    "municipios/localidades",
                    ". Esta lista refleja los documentos disponibles actualmente y no " +
                    "necesariamente el registro completo de municipios de la provincia.");
            }

            // --------------------------------------------------------
            // LOCALIDADES
            // --------------------------------------------------------

            if (EsConsultaLocalidades(n))
            {
                return ConstruirRespuestaListaLocalidades(
                    documentos,
                    "localidades",
                    ". La lista corresponde a los documentos disponibles actualmente.");
            }

            // --------------------------------------------------------
            // POBLACIÓN
            // --------------------------------------------------------

            if (EsConsultaPoblacion(n))
            {
                return BuscarRespuestaPoblacion(
                    documentos,
                    n);
            }

            // --------------------------------------------------------
            // UBICACIÓN
            // --------------------------------------------------------

            if (EsConsultaUbicacion(n))
            {
                return BuscarRespuestaUbicacion(
                    documentos,
                    n);
            }

            // --------------------------------------------------------
            // DEFINICIÓN
            // --------------------------------------------------------

            if (EsConsultaDefinicion(n))
            {
                return BuscarRespuestaDefinicion(
                    documentos,
                    n);
            }

            // --------------------------------------------------------
            // FECHA
            // --------------------------------------------------------

            if (EsConsultaFecha(n))
            {
                return BuscarRespuestaFecha(
                    documentos,
                    n);
            }

            return null;
        }

        /// <summary>
        /// Busca el documento del corpus más relevante para una
        /// consulta general.
        /// </summary>
        public async Task<(string Titulo, string Contenido)?>
            BuscarContextoGeneralAsync(string consulta)
        {
            if (string.IsNullOrWhiteSpace(consulta))
                return null;

            var n = NormalizarTexto(consulta);

            if (EsGobernadorActual(n))
                return null;

            var documentos =
                await ObtenerDocumentosAsync();

            if (documentos.Count == 0)
                return null;

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
                        score += 50;

                    if (documento.ContenidoTokens.Contains(palabra))
                        score += 5;
                }

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

            return (
                mejor.Titulo,
                mejor.Contenido);
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
                ContieneFrase(n, "provincia");

            bool mencionaCorrientes =
                ContieneFrase(n, "corrientes");

            bool mencionaCiudad =
                ContieneFrase(n, "capital") ||
                (
                    ContieneFrase(n, "ciudad") &&
                    mencionaCorrientes
                );

            var localidades =
                Localidades
                    .Where(l =>
                        ContieneFrase(n, l))
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
                var (oracion, score) =
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
        // LISTAS DE LOCALIDADES / MUNICIPIOS
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
            // Sólo se aplica el filtro natural si la consulta
            // realmente pide naturaleza o lugares naturales.
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

            // Turismo general.
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
            var entidades =
                ObtenerEntidadesConsulta(n);

            if (entidades.Count == 0)
                return null;

            var mejor =
                MejorDocumentoPorEntidades(
                    documentos,
                    entidades,
                    pesoContenido: 30,
                    bonus: d =>
                    {
                        int b = 0;

                        if (ContieneFrase(
                            d.ContenidoNorm,
                            "ubicado"))
                        {
                            b += 15;
                        }

                        if (ContieneFrase(
                            d.ContenidoNorm,
                            "ubicada"))
                        {
                            b += 15;
                        }

                        if (ContieneFrase(
                            d.ContenidoNorm,
                            TituloProvincia))
                        {
                            b += 10;
                        }

                        return b;
                    });

            if (mejor == null)
                return null;

            var oracion =
                BuscarOracionUbicacion(
                    mejor.Contenido);
            Console.WriteLine(
    $"[UBICACION-DOC] {mejor.Titulo}");
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
    var oraciones = SepararOraciones(contenido);

    if (oraciones.Count == 0)
        return null;

    string? mejor = null;
    int mejorPuntaje = 0;

    foreach (var oracion in oraciones)
    {
        var norm = NormalizarTexto(oracion);
        int puntaje = 0;
        if (ContieneFrase(norm, "en la provincia de corrientes"))
    puntaje += 200;

if (ContieneFrase(norm, "provincia de corrientes"))
    puntaje += 180;

if (ContieneFrase(norm, "en el nordeste de argentina"))
    puntaje += 150;

if (ContieneFrase(norm, "en el noreste de argentina"))
    puntaje += 150;

        // ------------------------------------------------------------
        // FORMAS DIRECTAS DE UBICACIÓN
        // ------------------------------------------------------------

        if (ContieneFrase(norm, "se encuentra"))
            puntaje += 80;

        if (ContieneFrase(norm, "se encuentran"))
            puntaje += 80;

        if (ContieneFrase(norm, "se ubica"))
            puntaje += 80;

        if (ContieneFrase(norm, "se ubican"))
            puntaje += 80;

        if (ContieneFrase(norm, "esta ubicado"))
            puntaje += 75;

        if (ContieneFrase(norm, "esta ubicada"))
            puntaje += 75;

        if (ContieneFrase(norm, "situado en"))
            puntaje += 75;

        if (ContieneFrase(norm, "situada en"))
            puntaje += 75;

        if (ContieneFrase(norm, "situados en"))
            puntaje += 75;

        if (ContieneFrase(norm, "situadas en"))
            puntaje += 75;

        // ------------------------------------------------------------
        // REFERENCIAS GEOGRÁFICAS
        // ------------------------------------------------------------

        if (ContieneFrase(norm, "en la provincia"))
            puntaje += 55;

        if (ContieneFrase(norm, "en el departamento"))
            puntaje += 45;

        if (ContieneFrase(norm, "provincia de corrientes"))
            puntaje += 50;

        if (ContieneFrase(norm, "corrientes"))
            puntaje += 20;

        if (ContieneFrase(norm, "argentina"))
            puntaje += 15;

        if (ContieneFrase(norm, "nordeste"))
            puntaje += 25;

        if (ContieneFrase(norm, "noreste"))
            puntaje += 25;

        if (ContieneFrase(norm, "norte"))
            puntaje += 15;

        if (ContieneFrase(norm, "sur"))
            puntaje += 15;

        if (ContieneFrase(norm, "este"))
            puntaje += 10;

        if (ContieneFrase(norm, "oeste"))
            puntaje += 10;

        // ------------------------------------------------------------
        // EXPRESIONES GEOGRÁFICAS
        // ------------------------------------------------------------

        if (ContieneFrase(norm, "en el nordeste de"))
            puntaje += 40;

        if (ContieneFrase(norm, "en el noreste de"))
            puntaje += 40;

        if (ContieneFrase(norm, "al norte de"))
            puntaje += 30;

        if (ContieneFrase(norm, "al sur de"))
            puntaje += 30;

        if (ContieneFrase(norm, "cerca de"))
            puntaje += 20;

        // ------------------------------------------------------------
        // EVITAR FRASES QUE SON PRINCIPALMENTE DESCRIPTIVAS
        // ------------------------------------------------------------

        if (ContieneFrase(norm, "humedal"))
            puntaje -= 10;

        if (ContieneFrase(norm, "fauna"))
            puntaje -= 10;

        if (ContieneFrase(norm, "flora"))
            puntaje -= 10;

        if (ContieneFrase(norm, "especies"))
            puntaje -= 10;

        Console.WriteLine(
    $"[UBICACION] {puntaje} => {oracion}");
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

            var mejor =
                MejorDocumentoPorEntidades(
                    documentos,
                    entidades,
                    pesoContenido: 25,
                    bonus: d =>
                    {
                        int b = 0;

                        if (ContieneFrase(
                            d.ContenidoNorm,
                            "es un"))
                        {
                            b += 15;
                        }

                        if (ContieneFrase(
                            d.ContenidoNorm,
                            "es una"))
                        {
                            b += 15;
                        }

                        return b;
                    });

            if (mejor == null)
                return null;

            // IMPORTANTE:
            // La oración de definición debe mencionar también
            // la entidad consultada. Esto evita errores como:
            //
            // "¿Qué es el Iberá?"
            // -> "El carpincho es un roedor herbívoro."
            //
            // aunque esa oración esté dentro del mismo documento.
            var oracion =
                BuscarOracionDefinicion(
                    mejor.Contenido,
                    entidades);

            if (string.IsNullOrWhiteSpace(oracion))
            {
                // Si no encontramos una definición explícita que
                // mencione la entidad, usamos la primera oración
                // del documento, que normalmente representa mejor
                // el tema principal del artículo.
                oracion =
                    PrimeraOracion(
                        mejor.Contenido);
            }

            return string.IsNullOrWhiteSpace(oracion)
                ? null
                : $"{mejor.Titulo}: {oracion}";
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
                ContieneFrase(norm, entidad)))
        {
            continue;
        }

        int puntaje = 0;

        // La entidad aparece como sujeto al comienzo.
        foreach (var entidad in entidades)
        {
            if (ContieneFrase(norm, entidad))
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

        // Definiciones explícitas.
        if (ContieneFrase(norm, "se denomina"))
            puntaje += 120;

        if (ContieneFrase(norm, "se define como"))
            puntaje += 120;

        if (ContieneFrase(norm, "es un"))
            puntaje += 80;

        if (ContieneFrase(norm, "es una"))
            puntaje += 80;

        // Formas típicas de descripción conceptual.
        if (ContieneFrase(norm, "se trata de"))
            puntaje += 70;

        if (ContieneFrase(norm, "consiste en"))
            puntaje += 70;

        // Penalizar oraciones que hablan principalmente
        // de fauna, turismo o actividades posteriores.
        if (ContieneFrase(norm, "yaguarete"))
            puntaje -= 40;

        if (ContieneFrase(norm, "fauna"))
            puntaje -= 20;

        if (ContieneFrase(norm, "flora"))
            puntaje -= 20;

        if (ContieneFrase(norm, "turismo"))
            puntaje -= 20;

        if (ContieneFrase(norm, "visitante"))
            puntaje -= 20;

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
                        ContieneFecha(
                            d.ContenidoNorm)
                            ? 20
                            : 0);

            if (mejor == null)
                return null;

            foreach (var oracion in
                SepararOraciones(mejor.Contenido))
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
                    .Select(d => d.Titulo)
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

            // Si hay una entidad específica además de "corrientes",
            // "corrientes" no aporta precisión al ranking.
            if (entidades.Count > 1)
                entidades.Remove("corrientes");

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
                : SepararOraciones(contenido)
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
                NormalizarTexto(titulo);

            var contenidoNorm =
                NormalizarTexto(contenido);

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