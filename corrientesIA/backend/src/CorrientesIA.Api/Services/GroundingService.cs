using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CorrientesIA.Data;
using CorrientesIA.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CorrientesIA.Api.Services
{
    public class GroundingService
    {
        private readonly AppDbContext _db;
        private readonly IMemoryCache? _cache;

        private const int CacheMinutes = 5;
        private const int MaxOracionesContextoGeneral = 5;
        private const int MinScoreGeneral = 10;

        private const string CacheDocumentos =
            "corrientesia_documentos";

        private const string CacheDatosDuros =
            "corrientesia_datos_duros";

        private const string CacheLugares =
            "corrientesia_lugares";

        // ============================================================
        // LOCALIDADES
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
            "santo tome",
            "concepcion",
            "sauce",
            "empedrado",
            "caa cati",
            "san cosme",
            "la cruz",
            "corrientes"
        };

        // ============================================================
        // ENTIDADES
        // ============================================================

        private static readonly string[] EntidadesOrdenadas =
            Localidades
                .Concat(
                    new[]
                    {
                        // Turismo y naturaleza
                        "esteros del ibera",
                        "parque nacional ibera",
                        "parque nacional mburucuya",
                        "fiesta nacional del chamame",
                        "chamame",

                        // Historia
                        "guerra de la triple alianza",
                        "historia de la provincia de corrientes",
                        "pedro ferre",

                        // Naturaleza / fauna
                        "rio parana",
                        "yaguarete",
                        "carpincho",
                        "ciervo de los pantanos",

                        // Cultura
                        "paye",
                        "guarani",
                        "chipa",
                        "sopa paraguaya",

                        // Entidades generales
                        "ibera",
                        "mburucuya"
                    })
                .OrderByDescending(e => e.Length)
                .ToArray();

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
        // INVALIDAR CACHE
        // ============================================================

        public void InvalidarCache()
        {
            _cache?.Remove(CacheDocumentos);
            _cache?.Remove(CacheDatosDuros);
            _cache?.Remove(CacheLugares);
        }

        // ============================================================
        // DATOS DUROS
        // ============================================================

        private async Task<List<DatoDuro>> ObtenerDatosDurosAsync()
        {
            if (_cache != null &&
                _cache.TryGetValue(
                    CacheDatosDuros,
                    out List<DatoDuro>? cacheados) &&
                cacheados != null)
            {
                return cacheados;
            }

            var datos =
                await _db.DatosDuros
                    .AsNoTracking()
                    .ToListAsync();

            if (_cache != null)
            {
                _cache.Set(
                    CacheDatosDuros,
                    datos,
                    TimeSpan.FromMinutes(CacheMinutes));
            }

            return datos;
        }

        // ============================================================
        // LUGARES
        // ============================================================

        private async Task<List<Lugar>> ObtenerLugaresAsync()
        {
            if (_cache != null &&
                _cache.TryGetValue(
                    CacheLugares,
                    out List<Lugar>? cacheados) &&
                cacheados != null)
            {
                return cacheados;
            }

            var lugares =
                await _db.Lugares
                    .AsNoTracking()
                    .ToListAsync();

            if (_cache != null)
            {
                _cache.Set(
                    CacheLugares,
                    lugares,
                    TimeSpan.FromMinutes(CacheMinutes));
            }

            return lugares;
        }

        // ============================================================
        // DOCUMENTOS
        // ============================================================

        private async Task<List<CorpusDocumento>> ObtenerDocumentosAsync()
        {
            if (_cache != null &&
                _cache.TryGetValue(
                    CacheDocumentos,
                    out List<CorpusDocumento>? cacheados) &&
                cacheados != null)
            {
                return cacheados;
            }

            var documentos =
                await _db.CorpusDocumentos
                    .AsNoTracking()
                    .ToListAsync();

            if (_cache != null)
            {
                _cache.Set(
                    CacheDocumentos,
                    documentos,
                    TimeSpan.FromMinutes(CacheMinutes));
            }

            return documentos;
        }

        // ============================================================
        // BUSQUEDA POR PALABRAS CLAVE
        // ============================================================

        public async Task<string?> BuscarPorPalabrasClaveAsync(
            string consulta)
        {
            if (string.IsNullOrWhiteSpace(consulta))
                return null;

            var normalizada =
                NormalizarTexto(consulta);

            // --------------------------------------------------------
            // DATOS DUROS
            // --------------------------------------------------------

            var datosDuros =
                await ObtenerDatosDurosAsync();

            var mejorDato =
                datosDuros
                    .Select(d => new
                    {
                        Dato = d,
                        Score =
                            PuntuarCoincidenciaDatoDuro(
                                normalizada,
                                NormalizarTexto(d.Clave))
                    })
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

            if (mejorDato != null &&
                !string.IsNullOrWhiteSpace(
                    mejorDato.Dato.Valor))
            {
                return mejorDato.Dato.Valor;
            }

            // --------------------------------------------------------
            // LUGARES
            // --------------------------------------------------------

            var lugares =
                await ObtenerLugaresAsync();

            foreach (var lugar in lugares)
            {
                var nombre =
                    NormalizarTexto(lugar.Nombre);

                if (string.IsNullOrWhiteSpace(nombre))
                    continue;

                if (!ContieneFrase(
                    normalizada,
                    nombre))
                {
                    continue;
                }

                var partes =
                    new List<string>();

                if (!string.IsNullOrWhiteSpace(
                    lugar.Nombre))
                {
                    partes.Add(
                        $"{lugar.Nombre}:");
                }

                if (!string.IsNullOrWhiteSpace(
                    lugar.Descripcion))
                {
                    partes.Add(
                        lugar.Descripcion);
                }

                if (!string.IsNullOrWhiteSpace(
                    lugar.Categoria))
                {
                    partes.Add(
                        $"Categoría: {lugar.Categoria}.");
                }

                var resultado =
                    string.Join(
                        " ",
                        partes);

                if (!string.IsNullOrWhiteSpace(
                    resultado))
                {
                    return resultado;
                }
            }

            return null;
        }

        // ============================================================
        // BUSQUEDA EN CORPUS
        // ============================================================

        public async Task<string?> BuscarEnCorpusAsync(
            string consulta)
        {
            if (string.IsNullOrWhiteSpace(consulta))
                return null;

            var contexto =
                await BuscarContextoGeneralAsync(
                    consulta);

            if (contexto == null)
                return null;

            return $"{contexto.Value.Titulo}: " +
                   contexto.Value.Contenido;
        }

        // ============================================================
        // BUSQUEDA DE CONTEXTO GENERAL
        //
        // IMPORTANTE:
        // El ChatController actual espera:
        //
        // (string Titulo, string Contenido)?
        // ============================================================

        public async Task<(string Titulo, string Contenido)?>
            BuscarContextoGeneralAsync(
                string consulta)
        {
            if (string.IsNullOrWhiteSpace(consulta))
                return null;

            var normalizada =
                NormalizarTexto(consulta);

            var documentos =
                await ObtenerDocumentosAsync();

            if (documentos.Count == 0)
                return null;

            var palabrasRelevantes =
                ObtenerPalabrasRelevantes(
                    normalizada);

            var entidades =
                ObtenerEntidadesConsulta(
                    normalizada);

            bool consultaHistoria =
                ContieneAlguna(
                    normalizada,
                    "historia",
                    "historico",
                    "historica",
                    "origen",
                    "origenes",
                    "fundacion",
                    "fundada",
                    "guerra",
                    "batalla");

            bool consultaTurismo =
                ContieneAlguna(
                    normalizada,
                    "turismo",
                    "turistico",
                    "turistica",
                    "visitar",
                    "visita",
                    "conocer",
                    "atractivo",
                    "atractivos",
                    "que hacer");

            bool consultaNaturaleza =
                ContieneAlguna(
                    normalizada,
                    "naturaleza",
                    "humedal",
                    "fauna",
                    "flora",
                    "animal",
                    "animales",
                    "parque",
                    "esteros");

            bool consultaPoblacion =
                ContieneAlguna(
                    normalizada,
                    "poblacion",
                    "habitantes",
                    "cuantos habitantes",
                    "censo",
                    "demografia");

            // ========================================================
            // PUNTUAR DOCUMENTOS
            // ========================================================

            var candidatos =
                new List<
                    (CorpusDocumento Documento, int Score)
                >();

            foreach (var documento in documentos)
            {
                if (string.IsNullOrWhiteSpace(
                    documento.Contenido))
                {
                    continue;
                }

                var titulo =
                    NormalizarTexto(
                        documento.Titulo);

                var contenido =
                    NormalizarTexto(
                        documento.Contenido);

                int score = 0;

                // ----------------------------------------------------
                // PALABRAS EN TITULO
                // ----------------------------------------------------

                foreach (var palabra
                    in palabrasRelevantes)
                {
                    if (ContieneFrase(
                        titulo,
                        palabra))
                    {
                        score += 100;
                    }
                }

                // ----------------------------------------------------
                // ENTIDADES
                // ----------------------------------------------------

                foreach (var entidad
                    in entidades)
                {
                    if (ContieneFrase(
                        titulo,
                        entidad))
                    {
                        score += 300;
                    }

                    if (ContieneFrase(
                        contenido,
                        entidad))
                    {
                        score += 25;
                    }
                }

                // ----------------------------------------------------
                // PALABRAS EN CONTENIDO
                // ----------------------------------------------------

                foreach (var palabra
                    in palabrasRelevantes)
                {
                    if (ContieneFrase(
                        contenido,
                        palabra))
                    {
                        score += 3;
                    }
                }

                // ----------------------------------------------------
                // HISTORIA
                // ----------------------------------------------------

                if (consultaHistoria)
                {
                    if (ContieneAlguna(
                        titulo,
                        "historia",
                        "guerra",
                        "batalla",
                        "origen"))
                    {
                        score += 80;
                    }

                    if (ContieneAlguna(
                        contenido,
                        "historia",
                        "guerra",
                        "batalla",
                        "siglo",
                        "fundacion"))
                    {
                        score += 20;
                    }
                }

                // ----------------------------------------------------
                // TURISMO
                // ----------------------------------------------------

                if (consultaTurismo)
                {
                    if (ContieneAlguna(
                        titulo,
                        "turismo",
                        "esteros",
                        "parque",
                        "chamame"))
                    {
                        score += 80;
                    }

                    if (ContieneAlguna(
                        contenido,
                        "turismo",
                        "turistico",
                        "atractivos"))
                    {
                        score += 15;
                    }
                }

                // ----------------------------------------------------
                // NATURALEZA
                // ----------------------------------------------------

                if (consultaNaturaleza)
                {
                    if (ContieneAlguna(
                        titulo,
                        "esteros",
                        "parque",
                        "yaguarete",
                        "carpincho",
                        "naturaleza"))
                    {
                        score += 80;
                    }

                    if (ContieneAlguna(
                        contenido,
                        "humedal",
                        "fauna",
                        "flora",
                        "naturaleza"))
                    {
                        score += 15;
                    }
                }

                // ----------------------------------------------------
                // PENALIZAR DEMOGRAFIA
                // ----------------------------------------------------

                if (!consultaPoblacion &&
                    ContieneAlguna(
                        titulo,
                        "poblacion",
                        "censo",
                        "demografia"))
                {
                    score -= 80;
                }

                // ----------------------------------------------------
                // SI HAY ENTIDADES, PRIORIZAR TITULO
                // ----------------------------------------------------

                if (entidades.Count > 0)
                {
                    bool tieneEntidadEnTitulo =
                        entidades.Any(e =>
                            ContieneFrase(
                                titulo,
                                e));

                    if (!tieneEntidadEnTitulo)
                    {
                        score -= 60;
                    }
                }

                if (score > MinScoreGeneral)
                {
                    candidatos.Add(
                        (documento, score));
                }
            }

            if (candidatos.Count == 0)
                return null;

            var mejor =
                candidatos
                    .OrderByDescending(
                        x => x.Score)
                    .First();

            // ========================================================
            // CONSTRUIR CONTEXTO
            // ========================================================

            var contexto =
                ConstruirContextoRelevante(
                    mejor.Documento,
                    normalizada,
                    palabrasRelevantes,
                    entidades);

            if (string.IsNullOrWhiteSpace(
                contexto))
            {
                return null;
            }

            Console.WriteLine(
                $"[GENERAL-DOC] " +
                $"{mejor.Documento.Titulo} | " +
                $"Score={mejor.Score}");

            Console.WriteLine(
                $"[GENERAL-CONTEXTO] {contexto}");

            return (
                mejor.Documento.Titulo,
                contexto
            );
        }

        // ============================================================
        // CONSTRUIR CONTEXTO RELEVANTE
        // ============================================================

        private static string ConstruirContextoRelevante(
            CorpusDocumento documento,
            string consulta,
            List<string> palabrasRelevantes,
            List<string> entidades)
        {
            if (string.IsNullOrWhiteSpace(
                documento.Contenido))
            {
                return string.Empty;
            }

            var oraciones =
                SepararOraciones(
                    documento.Contenido)
                .Where(o => o.Length >= 55)
                .ToList();

            if (oraciones.Count == 0)
            {
                return documento.Contenido.Trim();
            }

            bool consultaHistoria =
                ContieneAlguna(
                    consulta,
                    "historia",
                    "historico",
                    "historica",
                    "origen",
                    "origenes",
                    "fundacion",
                    "fundada",
                    "guerra",
                    "batalla");

            bool consultaTurismo =
                ContieneAlguna(
                    consulta,
                    "turismo",
                    "turistico",
                    "turistica",
                    "visitar",
                    "visita",
                    "atractivo",
                    "atractivos",
                    "que hacer");

            bool consultaNaturaleza =
                ContieneAlguna(
                    consulta,
                    "naturaleza",
                    "humedal",
                    "fauna",
                    "flora",
                    "animal",
                    "animales",
                    "parque",
                    "esteros");

            var puntuadas =
                new List<
                    (string Oracion, int Score, int Indice)
                >();

            for (int i = 0;
                 i < oraciones.Count;
                 i++)
            {
                var oracion =
                    oraciones[i];

                var normalizada =
                    NormalizarTexto(
                        oracion);

                int score = 0;

                // ----------------------------------------------------
                // ENTIDADES
                // ----------------------------------------------------

                foreach (var entidad
                    in entidades)
                {
                    if (ContieneFrase(
                        normalizada,
                        entidad))
                    {
                        score += 80;

                        if (normalizada.StartsWith(
                            entidad))
                        {
                            score += 40;
                        }
                    }
                }

                // ----------------------------------------------------
                // PALABRAS RELEVANTES
                // ----------------------------------------------------

                foreach (var palabra
                    in palabrasRelevantes)
                {
                    if (ContieneFrase(
                        normalizada,
                        palabra))
                    {
                        score += 12;
                    }
                }

                // ----------------------------------------------------
                // HISTORIA
                // ----------------------------------------------------

                if (consultaHistoria)
                {
                    if (ContieneAlguna(
                        normalizada,
                        "historia",
                        "historico",
                        "historica",
                        "guerra",
                        "batalla",
                        "origen",
                        "fundacion",
                        "fundada"))
                    {
                        score += 30;
                    }

                    // Prioridad especial:
                    // relación del acontecimiento con Corrientes.
                    if (ContieneFrase(
                        normalizada,
                        "corrientes"))
                    {
                        score += 45;
                    }

                    if (ContieneAlguna(
                        normalizada,
                        "provincia",
                        "territorio",
                        "parana",
                        "paraguay"))
                    {
                        score += 15;
                    }
                }

                // ----------------------------------------------------
                // TURISMO
                // ----------------------------------------------------

                if (consultaTurismo &&
                    ContieneAlguna(
                        normalizada,
                        "turismo",
                        "turistico",
                        "turistica",
                        "atractivo",
                        "atractivos",
                        "visitar",
                        "visita"))
                {
                    score += 30;
                }

                // ----------------------------------------------------
                // NATURALEZA
                // ----------------------------------------------------

                if (consultaNaturaleza &&
                    ContieneAlguna(
                        normalizada,
                        "naturaleza",
                        "humedal",
                        "fauna",
                        "flora",
                        "parque",
                        "esteros"))
                {
                    score += 30;
                }

                // ----------------------------------------------------
                // PENALIZAR SECCIONES POCO UTILES
                // ----------------------------------------------------

                if (ContieneAlguna(
                    normalizada,
                    "referencias",
                    "vease tambien",
                    "enlaces externos",
                    "bibliografia",
                    "categorias"))
                {
                    score -= 30;
                }

                // ----------------------------------------------------
                // DEMOGRAFIA
                // ----------------------------------------------------

                bool consultaPoblacion =
                    ContieneAlguna(
                        consulta,
                        "poblacion",
                        "habitantes",
                        "censo",
                        "demografia");

                if (!consultaPoblacion &&
                    ContieneAlguna(
                        normalizada,
                        "habitantes",
                        "poblacion",
                        "censo"))
                {
                    score -= 20;
                }

                if (score > 0)
                {
                    puntuadas.Add(
                        (
                            oracion,
                            score,
                            i
                        ));
                }
            }

            // ========================================================
            // SELECCION PRINCIPAL
            // ========================================================

            var seleccionadas =
                puntuadas
                    .OrderByDescending(
                        x => x.Score)
                    .Take(
                        MaxOracionesContextoGeneral)
                    .ToList();

            // ========================================================
            // HISTORIA
            // ========================================================

            if (consultaHistoria)
            {
                var historicas =
                    puntuadas
                        .Where(x =>
                            ContieneAlguna(
                                NormalizarTexto(
                                    x.Oracion),
                                "historia",
                                "historico",
                                "historica",
                                "guerra",
                                "batalla",
                                "origen",
                                "fundacion",
                                "fundada"))
                        .OrderByDescending(
                            x => x.Score)
                        .Take(
                            MaxOracionesContextoGeneral)
                        .ToList();

                if (historicas.Count > 0)
                {
                    seleccionadas =
                        historicas;
                }
            }

            // ========================================================
            // FALLBACK
            // ========================================================

            if (seleccionadas.Count == 0)
            {
                return oraciones
                    .FirstOrDefault()
                    ?? string.Empty;
            }

            // ========================================================
            // ELIMINAR DUPLICADOS
            // ========================================================

            var resultado =
                seleccionadas
                    .GroupBy(
                        x => NormalizarTexto(
                            x.Oracion))
                    .Select(
                        g => g.First())
                    .OrderBy(
                        x => x.Indice)
                    .Select(
                        x => x.Oracion.Trim())
                    .ToList();

            // ========================================================
            // DEBUG
            // ========================================================

            Console.WriteLine(
                "========== CONTEXTO SELECCIONADO ==========");

            foreach (var oracion in resultado)
            {
                Console.WriteLine(
                    $"[CONTEXTO] {oracion}");
            }

            Console.WriteLine(
                "===========================================");

            return string.Join(
                " ",
                resultado);
        }

        // ============================================================
        // ENTIDADES DE CONSULTA
        // ============================================================

        private static List<string>
            ObtenerEntidadesConsulta(
                string normalizada)
        {
            var entidades =
                new List<string>();

            foreach (var entidad
                in EntidadesOrdenadas)
            {
                if (!ContieneFrase(
                    normalizada,
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

                entidades.Add(
                    entidad);
            }

            // NO eliminar "corrientes".
            //
            // En una consulta:
            // "¿Qué pasó en Corrientes durante
            //  la Guerra de la Triple Alianza?"
            //
            // necesitamos conservar las dos entidades.

            return entidades;
        }

        // ============================================================
        // PALABRAS RELEVANTES
        // ============================================================

        private static List<string>
            ObtenerPalabrasRelevantes(
                string texto)
        {
            var stopWords =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    "que",
                    "cual",
                    "como",
                    "donde",
                    "cuando",
                    "quien",
                    "para",
                    "por",
                    "con",
                    "sin",
                    "sobre",
                    "entre",
                    "desde",
                    "hacia",
                    "del",
                    "las",
                    "los",
                    "una",
                    "uno",
                    "unos",
                    "unas",
                    "esta",
                    "este",
                    "estos",
                    "estas",
                    "fue",
                    "son",
                    "era",
                    "hay",
                    "tiene",
                    "tuvo",
                    "paso",
                    "en",
                    "la",
                    "el",
                    "de",
                    "y",
                    "o",
                    "a"
                };

            return Regex
                .Split(
                    texto,
                    @"\s+")
                .Select(
                    NormalizarTexto)
                .Where(
                    x => x.Length >= 3)
                .Where(
                    x => !stopWords.Contains(x))
                .Distinct()
                .ToList();
        }

        // ============================================================
        // PUNTUACION DATOS DUROS
        // ============================================================

        private static int
            PuntuarCoincidenciaDatoDuro(
                string consulta,
                string clave)
        {
            if (string.IsNullOrWhiteSpace(
                consulta) ||
                string.IsNullOrWhiteSpace(
                    clave))
            {
                return 0;
            }

            if (ContieneFrase(
                consulta,
                clave))
            {
                return 100;
            }

            var palabras =
                clave.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries);

            int score = 0;

            foreach (var palabra
                in palabras)
            {
                if (palabra.Length >= 3 &&
                    ContieneFrase(
                        consulta,
                        palabra))
                {
                    score += 10;
                }
            }

            return score;
        }

        // ============================================================
        // NORMALIZACION
        // ============================================================

        private static string
            NormalizarTexto(
                string? texto)
        {
            if (string.IsNullOrWhiteSpace(
                texto))
            {
                return string.Empty;
            }

            var normalizado =
                texto.Trim()
                    .ToLowerInvariant();

            normalizado =
                normalizado
                    .Replace('á', 'a')
                    .Replace('é', 'e')
                    .Replace('í', 'i')
                    .Replace('ó', 'o')
                    .Replace('ú', 'u')
                    .Replace('ü', 'u')
                    .Replace('ñ', 'n');

            normalizado =
                Regex.Replace(
                    normalizado,
                    @"\s+",
                    " ");

            return normalizado;
        }

        // ============================================================
        // CONTIENE FRASE
        // ============================================================

        private static bool
            ContieneFrase(
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
                NormalizarTexto(
                    texto);

            var fraseNormalizada =
                NormalizarTexto(
                    frase);

            return Regex.IsMatch(
                textoNormalizado,
                $@"(?<!\w){Regex.Escape(fraseNormalizada)}(?!\w)",
                RegexOptions.CultureInvariant);
        }

        // ============================================================
        // CONTIENE ALGUNA
        // ============================================================

        private static bool
            ContieneAlguna(
                string texto,
                params string[] frases)
        {
            foreach (var frase
                in frases)
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

        // ============================================================
        // SEPARAR ORACIONES
        // ============================================================

        private static List<string>
            SepararOraciones(
                string contenido)
        {
            if (string.IsNullOrWhiteSpace(
                contenido))
            {
                return new List<string>();
            }

            // Evita separar números como:
            // 358.223
            // 12.000
            // 1.500

            var partes =
                Regex.Split(
                    contenido.Trim(),
                    @"(?<=[.!?])\s+(?=[A-ZÁÉÍÓÚÜÑ0-9])");

            return partes
                .Select(
                    x => x.Trim())
                .Where(
                    x =>
                        !string.IsNullOrWhiteSpace(
                            x))
                .ToList();
        }
    }
}