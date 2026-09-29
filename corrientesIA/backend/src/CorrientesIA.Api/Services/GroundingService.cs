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
    /// Búsqueda de "grounding" sobre datos duros, lugares y corpus.
    ///
    /// Cambios principales respecto a la versión anterior:
    ///  - El texto de la consulta se normaliza UNA sola vez.
    ///  - Tablas y corpus se indexan (normalizados + tokens) y se cachean.
    ///  - Coincidencia por palabra/frase completa (sin falsos positivos por substring).
    ///  - Separación de oraciones que no rompe números como "358.223".
    ///  - Los bonus de ranking sólo aplican si hubo coincidencia real con una entidad.
    ///  - Población: ya no devuelve el documento de otra localidad.
    /// </summary>
    public class GroundingService
    {
        private readonly AppDbContext _db;
        private readonly IMemoryCache? _cache;

        // ------------------------------------------------------------
        // Configuración
        // ------------------------------------------------------------

        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

        private const string CacheDocumentos = "grounding:documentos";
        private const string CacheDatosDuros = "grounding:datosduros";
        private const string CacheLugares = "grounding:lugares";

        private const string TituloCiudad = "corrientes ciudad";
        private const string TituloProvincia = "provincia de corrientes";

        private const int LongitudMinimaValorDatoDuro = 3;
        private const int PuntajeMinimoGeneral = 10;

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

        // Ordenadas de mayor a menor longitud para detectar primero las más específicas.
        private static readonly string[] EntidadesOrdenadas = Localidades
            .Concat(new[]
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

        private static readonly HashSet<string> PalabrasGenericas = new(
            new[]
            {
                "que", "cual", "cuales", "quien", "quienes", "como", "donde",
                "cuando", "porque", "para", "con", "por", "del", "de", "la",
                "las", "el", "los", "un", "una", "unos", "unas", "es", "son",
                "hay", "tiene", "tienen", "informacion", "sobre", "corrientes",
                "argentina", "provincia", "ciudad", "capital", "lugar",
                "lugares", "ubicado", "ubicada", "ubicacion", "localizado",
                "localizada", "localidad", "localidades", "municipio",
                "municipios", "poblacion", "habitantes", "actual",
                "actualmente", "vigente", "hoy", "conoce", "sabe"
            },
            StringComparer.Ordinal);

        // Años entre 1500 y 2100, con límites de palabra (no matchea "15000").
        private static readonly Regex RegexAnio = new(
            @"\b(1[5-9][0-9]{2}|20[0-9]{2}|2100)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Corta en . ! ? seguidos de espacio, o en saltos de línea.
        // No corta "358.223 habitantes" ni "1.5 millones".
        private static readonly Regex RegexOraciones = new(
            @"(?<=[.!?])\s+|[\r\n]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex RegexDigito = new(
            @"[0-9]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // ------------------------------------------------------------
        // Tipos de índice
        // ------------------------------------------------------------

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

        private sealed record DocumentoPuntuado(
            DocumentoIndexado Documento,
            int Score);

        // ------------------------------------------------------------
        // Constructor
        // ------------------------------------------------------------

        /// <param name="cache">
        /// Opcional. Si se registra AddMemoryCache() se cachean las tablas
        /// (si no, funciona igual pero consulta la BD en cada llamada).
        /// </param>
        public GroundingService(AppDbContext db, IMemoryCache? cache = null)
        {
            _db = db;
            _cache = cache;
        }

        /// <summary>Llamar después de modificar corpus, datos duros o lugares.</summary>
        public void InvalidarCache()
        {
            _cache?.Remove(CacheDocumentos);
            _cache?.Remove(CacheDatosDuros);
            _cache?.Remove(CacheLugares);
        }

        // ============================================================
        // API PÚBLICA
        // ============================================================

        public async Task<string?> BuscarDatoDuroAsync(string consulta)
        {
            if (string.IsNullOrWhiteSpace(consulta))
                return null;

            return await BuscarDatoDuroInternoAsync(NormalizarTexto(consulta));
        }

        public async Task<string?> BuscarLugaresAsync(string consulta)
        {
            if (string.IsNullOrWhiteSpace(consulta))
                return null;

            return await BuscarLugaresInternoAsync(NormalizarTexto(consulta));
        }

        public async Task<string?> BuscarPorPalabrasClaveAsync(string consulta)
        {
            if (string.IsNullOrWhiteSpace(consulta))
                return null;

            var n = NormalizarTexto(consulta);

            // Actualidad política: nunca devolver información histórica del corpus.
            if (EsGobernadorActual(n))
                return null;

            if (EsConsultaFaltante(n))
                return await ConstruirRespuestaSobreFaltantesAsync();

            if (EsMetaConocimiento(n))
                return await ConstruirRespuestaSobreConocimientoAsync();

            var datoDuro = await BuscarDatoDuroInternoAsync(n);
            if (!string.IsNullOrWhiteSpace(datoDuro))
                return datoDuro;

            // Lugares: sólo para consultas claramente referidas a un lugar concreto.
            if (EsConsultaUbicacion(n) || EsConsultaInformacionLugar(n))
                return await BuscarLugaresInternoAsync(n);

            return null;
        }

        public async Task<string?> BuscarEnCorpusAsync(string consulta)
        {
            if (string.IsNullOrWhiteSpace(consulta))
                return null;

            var n = NormalizarTexto(consulta);

            // El corpus contiene gobernadores históricos: jamás usarlos para "el actual".
            if (EsGobernadorActual(n))
                return null;

            if (EsConsultaFaltante(n))
                return await ConstruirRespuestaSobreFaltantesAsync();

            if (EsMetaConocimiento(n))
                return await ConstruirRespuestaSobreConocimientoAsync();

            var documentos = await ObtenerDocumentosAsync();
            if (documentos.Count == 0)
                return null;

            if (EsConsultaDiferenciaCiudadProvincia(n))
                return ConstruirRespuestaCiudadVsProvincia(documentos);

            if (EsConsultaMunicipios(n))
                return ConstruirRespuestaListaLocalidades(documentos, "municipios/localidades",
                    ". Esta lista refleja los documentos disponibles actualmente y no " +
                    "necesariamente el registro completo de municipios de la provincia.");

            if (EsConsultaLocalidades(n))
                return ConstruirRespuestaListaLocalidades(documentos, "localidades",
                    ". La lista corresponde a los documentos disponibles actualmente.");

            if (EsConsultaPoblacion(n))
                return BuscarRespuestaPoblacion(documentos, n);

            if (EsConsultaUbicacion(n))
                return BuscarRespuestaUbicacion(documentos, n);

            if (EsConsultaDefinicion(n))
                return BuscarRespuestaDefinicion(documentos, n);

            if (EsConsultaFecha(n))
                return BuscarRespuestaFecha(documentos, n);

            return BuscarRespuestaGeneral(documentos, n);
        }

        // ============================================================
        // DATOS DUROS
        // ============================================================

        private async Task<string?> BuscarDatoDuroInternoAsync(string n)
        {
            if (n.Length == 0)
                return null;

            var datos = await ObtenerDatosDurosAsync();

            DatoDuroIndexado? mejor = null;
            int mejorPeso = 0;

            foreach (var dato in datos)
            {
                if (dato.Palabras.Length == 0)
                    continue;

                int peso = 0;

                // Todas las palabras de la clave presentes: gana la clave más específica.
                if (dato.Palabras.All(p => ContieneFrase(n, p)))
                {
                    peso = 100 + dato.Palabras.Length;
                }
                // El valor aparece completo en la consulta (mínimo de longitud para evitar ruido).
                else if (dato.ValorNorm.Length >= LongitudMinimaValorDatoDuro &&
                         ContieneFrase(n, dato.ValorNorm))
                {
                    peso = 1;
                }

                if (peso > mejorPeso)
                {
                    mejorPeso = peso;
                    mejor = dato;
                }
            }

            return mejor == null ? null : $"{mejor.Clave}: {mejor.Valor}";
        }

        // ============================================================
        // LUGARES
        // ============================================================

        private async Task<string?> BuscarLugaresInternoAsync(string n)
        {
            if (n.Length == 0)
                return null;

            var lugares = await ObtenerLugaresAsync();

            LugarIndexado? mejor = null;
            int mejorPeso = 0;

            foreach (var lugar in lugares)
            {
                if (lugar.NombreNorm.Length == 0)
                    continue;

                bool coincideNombre = ContieneFrase(n, lugar.NombreNorm);

                bool coincideLocalidad =
                    lugar.LocalidadNorm.Length > 0 &&
                    ContieneFrase(n, lugar.LocalidadNorm);

                bool coincideCategoria =
                    lugar.CategoriaNorm.Length > 0 &&
                    ContieneFrase(n, lugar.CategoriaNorm);

                int peso = 0;

                if (coincideNombre)
                    peso = 1000 + lugar.NombreNorm.Length; // nombre más largo = más específico
                else if (coincideLocalidad && coincideCategoria)
                    peso = 1;

                if (peso > mejorPeso)
                {
                    mejorPeso = peso;
                    mejor = lugar;
                }
            }

            return mejor == null ? null : $"{mejor.Nombre}: {mejor.Descripcion}";
        }

        // ============================================================
        // POBLACIÓN
        // ============================================================

        private static string? BuscarRespuestaPoblacion(
            IReadOnlyList<DocumentoIndexado> documentos,
            string n)
        {
            bool mencionaProvincia = ContieneFrase(n, "provincia");
            bool mencionaCorrientes = ContieneFrase(n, "corrientes");
            bool mencionaCiudad =
                ContieneFrase(n, "capital") ||
                (ContieneFrase(n, "ciudad") && mencionaCorrientes);

            var localidades = Localidades.Where(l => ContieneFrase(n, l)).ToList();

            IEnumerable<DocumentoIndexado> candidatos;

            if (localidades.Count > 0)
            {
                // Antes se devolvía el primer documento con "habitantes", aunque fuera de otra localidad.
                candidatos = documentos.Where(d =>
                    localidades.Any(l => ContieneFrase(d.TituloNorm, l)));
            }
            else if (mencionaCiudad)
            {
                candidatos = documentos.Where(d => ContieneFrase(d.TituloNorm, TituloCiudad));
            }
            else if (mencionaProvincia)
            {
                candidatos = documentos.Where(d => ContieneFrase(d.TituloNorm, TituloProvincia));
            }
            else if (mencionaCorrientes)
            {
                candidatos = documentos.Where(d =>
                    ContieneFrase(d.TituloNorm, TituloCiudad) ||
                    ContieneFrase(d.TituloNorm, TituloProvincia));
            }
            else
            {
                // Sin objetivo identificable: mejor null (permite WebSearch) que un documento al azar.
                return null;
            }

            string? mejorRespuesta = null;
            int mejorScore = 0;

            foreach (var documento in candidatos)
            {
                var (oracion, score) = BuscarOracionPoblacion(documento.Contenido);

                if (oracion != null && score > mejorScore)
                {
                    mejorScore = score;
                    mejorRespuesta = $"{documento.Titulo}: {oracion}";
                }
            }

            return mejorRespuesta;
        }

        private static (string? Oracion, int Score) BuscarOracionPoblacion(string contenido)
        {
            string? mejor = null;
            int mejorScore = 0;

            foreach (var oracion in SepararOraciones(contenido))
            {
                var norm = NormalizarTexto(oracion);

                bool habitantes = ContieneFrase(norm, "habitantes");
                bool poblacion = ContieneFrase(norm, "poblacion");
                bool censo = ContieneFrase(norm, "censo");
                bool indec = ContieneFrase(norm, "indec");

                if (!habitantes && !poblacion && !censo && !indec)
                    continue;

                int score = 0;
                if (habitantes) score += 50;
                if (poblacion) score += 30;
                if (censo) score += 20;
                if (indec) score += 20;
                if (ContieneFecha(norm)) score += 10;
                if (RegexDigito.IsMatch(norm)) score += 25; // una cifra vale más que una frase vaga

                if (score > mejorScore)
                {
                    mejorScore = score;
                    mejor = oracion;
                }
            }

            return (mejor, mejorScore);
        }

        // ============================================================
        // LISTAS DE LOCALIDADES / MUNICIPIOS
        // ============================================================

        private static string? ConstruirRespuestaListaLocalidades(
            IReadOnlyList<DocumentoIndexado> documentos,
            string etiqueta,
            string cierre)
        {
            var nombres = documentos
                .Where(d => EsDocumentoLocalidad(d.TituloNorm))
                .Select(d => d.Titulo.Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (nombres.Count == 0)
                return null;

            return
                "En el corpus actual, CorrientesIA tiene información sobre " +
                etiqueta + " como " +
                string.Join(", ", nombres) +
                cierre;
        }

        private static bool EsDocumentoLocalidad(string tituloNorm) =>
            Localidades.Any(l => ContieneFrase(tituloNorm, l));

        // ============================================================
        // CIUDAD VS PROVINCIA
        // ============================================================

        private static string? ConstruirRespuestaCiudadVsProvincia(
            IReadOnlyList<DocumentoIndexado> documentos)
        {
            bool hayCiudad = documentos.Any(d => ContieneFrase(d.TituloNorm, TituloCiudad));
            bool hayProvincia = documentos.Any(d => ContieneFrase(d.TituloNorm, TituloProvincia));

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
        // UBICACIÓN / DEFINICIÓN / FECHA
        // ============================================================

        private static string? BuscarRespuestaUbicacion(
            IReadOnlyList<DocumentoIndexado> documentos,
            string n)
        {
            var entidades = ObtenerEntidadesConsulta(n);
            if (entidades.Count == 0)
                return null;

            var mejor = MejorDocumentoPorEntidades(
                documentos,
                entidades,
                pesoContenido: 30,
                bonus: d =>
                {
                    int b = 0;
                    if (ContieneFrase(d.ContenidoNorm, "ubicado")) b += 15;
                    if (ContieneFrase(d.ContenidoNorm, "ubicada")) b += 15;
                    if (ContieneFrase(d.ContenidoNorm, TituloProvincia)) b += 10;
                    return b;
                });

            if (mejor == null)
                return null;

            var oracion = BuscarOracionUbicacion(mejor.Contenido);

            if (string.IsNullOrWhiteSpace(oracion))
                oracion = PrimeraOracion(mejor.Contenido);

            return string.IsNullOrWhiteSpace(oracion)
                ? null
                : $"{mejor.Titulo}: {oracion}";
        }

        private static string? BuscarOracionUbicacion(string contenido)
        {
            string? mejor = null;
            int mejorScore = 0;

            foreach (var oracion in SepararOraciones(contenido))
            {
                var norm = NormalizarTexto(oracion);
                int score = 0;

                if (ContieneFrase(norm, "ubicado")) score += 50;
                if (ContieneFrase(norm, "ubicada")) score += 50;
                if (ContieneFrase(norm, "provincia")) score += 30;
                if (ContieneFrase(norm, "corrientes")) score += 20;
                if (ContieneFrase(norm, "departamento")) score += 15;

                if (score > mejorScore)
                {
                    mejorScore = score;
                    mejor = oracion;
                }
            }

            return mejor;
        }

        private static string? BuscarRespuestaDefinicion(
            IReadOnlyList<DocumentoIndexado> documentos,
            string n)
        {
            var entidades = ObtenerEntidadesConsulta(n);
            if (entidades.Count == 0)
                return null;

            var mejor = MejorDocumentoPorEntidades(
                documentos,
                entidades,
                pesoContenido: 25,
                bonus: d =>
                {
                    int b = 0;
                    if (ContieneFrase(d.ContenidoNorm, "es un")) b += 15;
                    if (ContieneFrase(d.ContenidoNorm, "es una")) b += 15;
                    return b;
                });

            if (mejor == null)
                return null;

            var oracion = BuscarOracionDefinicion(mejor.Contenido);

            if (string.IsNullOrWhiteSpace(oracion))
                oracion = PrimeraOracion(mejor.Contenido);

            return string.IsNullOrWhiteSpace(oracion)
                ? null
                : $"{mejor.Titulo}: {oracion}";
        }

        private static string? BuscarOracionDefinicion(string contenido)
        {
            foreach (var oracion in SepararOraciones(contenido))
            {
                var norm = NormalizarTexto(oracion);

                if (ContieneFrase(norm, "es un") || ContieneFrase(norm, "es una"))
                    return oracion;
            }

            return null;
        }

        private static string? BuscarRespuestaFecha(
            IReadOnlyList<DocumentoIndexado> documentos,
            string n)
        {
            var entidades = ObtenerEntidadesConsulta(n);
            if (entidades.Count == 0)
                return null;

            var mejor = MejorDocumentoPorEntidades(
                documentos,
                entidades,
                pesoContenido: 20,
                bonus: d => ContieneFecha(d.ContenidoNorm) ? 20 : 0);

            if (mejor == null)
                return null;

            foreach (var oracion in SepararOraciones(mejor.Contenido))
            {
                if (ContieneFecha(oracion))
                    return $"{mejor.Titulo}: {oracion}";
            }

            return null;
        }

        /// <summary>
        /// Ranking común. El "bonus" sólo se suma si el documento ya coincidió
        /// con alguna entidad (antes un bonus solo bastaba para entrar al ranking).
        /// </summary>
        private static DocumentoIndexado? MejorDocumentoPorEntidades(
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
                    if (ContieneFrase(documento.TituloNorm, entidad))
                        score += 100;

                    if (ContieneFrase(documento.ContenidoNorm, entidad))
                        score += pesoContenido;
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
        // CONSULTA GENERAL
        // ============================================================

        private static string? BuscarRespuestaGeneral(
            IReadOnlyList<DocumentoIndexado> documentos,
            string n)
        {
            var palabras = ObtenerPalabrasRelevantes(n);
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

            // Un único match débil en el contenido no es evidencia suficiente.
            if (mejor == null || mejorScore < PuntajeMinimoGeneral)
                return null;

            var oracion = ObtenerMejorOracion(mejor.Contenido, palabras);

            return string.IsNullOrWhiteSpace(oracion)
                ? null
                : $"{mejor.Titulo}: {oracion}";
        }

        // ============================================================
        // RESPUESTAS META
        // ============================================================

        private async Task<string?> ConstruirRespuestaSobreFaltantesAsync()
        {
            var documentos = await ObtenerDocumentosAsync();
            var datosDuros = await ObtenerDatosDurosAsync();

            var respuesta = new StringBuilder();

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

        private async Task<string?> ConstruirRespuestaSobreConocimientoAsync()
        {
            var documentos = await ObtenerDocumentosAsync();

            var titulos = documentos
                .Select(d => d.Titulo)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (titulos.Count == 0)
                return null;

            return
                $"Actualmente CorrientesIA tiene {titulos.Count} documentos en su corpus. " +
                $"Entre los temas disponibles se encuentran: {string.Join(", ", titulos)}.";
        }

        // ============================================================
        // DETECCIÓN DE INTENCIONES
        // (todas reciben texto YA normalizado)
        // ============================================================

        private static bool EsGobernadorActual(string n) =>
            EsConsultaGobernador(n) && EsConsultaActualidad(n);

        // "que informacion tiene corrientesia" es prefijo de la variante "... sobre X":
        // esa variante es una consulta de lugar, no una consulta meta.
        private static bool EsMetaConocimiento(string n) =>
            EsConsultaConocimiento(n) && !EsConsultaInformacionLugar(n);

        private static bool EsConsultaPoblacion(string n) =>
            ContieneAlguna(n, "poblacion", "habitantes", "censo");

        private static bool EsConsultaGobernador(string n) =>
            ContieneAlguna(n, "gobernador", "gobernadora");

        private static bool EsConsultaMunicipios(string n) =>
            ContieneAlguna(n, "municipio", "municipios");

        private static bool EsConsultaLocalidades(string n) =>
            ContieneAlguna(n, "localidad", "localidades");

        private static bool EsConsultaActualidad(string n) =>
            ContieneAlguna(n,
                "actual", "actualmente", "actualidad", "actuales",
                "hoy", "ahora", "vigente", "vigentes", "gobierna",
                "quien ocupa", "quien es el gobernador",
                "quien es el actual", "quien es la actual");

        private static bool EsConsultaFaltante(string n) =>
            ContieneAlguna(n,
                "que informacion no tiene",
                "que informacion le falta",
                "que datos no tiene",
                "que no tiene",
                "que no sabe",
                "que informacion falta",
                "que le falta",
                "que cosas no sabe");

        private static bool EsConsultaConocimiento(string n) =>
            ContieneAlguna(n,
                "que informacion tiene corrientesia",
                "que sabe corrientesia",
                "que conoce corrientesia",
                "que informacion conoce corrientesia",
                "que tiene corrientesia",
                "sobre que tiene informacion");

        private static bool EsConsultaUbicacion(string n) =>
            ContieneAlguna(n,
                "donde", "ubicado", "ubicada", "ubicacion",
                "localizado", "localizada");

        private static bool EsConsultaInformacionLugar(string n) =>
            ContieneAlguna(n,
                "informacion sobre",
                "que informacion tiene corrientesia sobre",
                "que informacion conoce corrientesia sobre",
                "que sabe corrientesia sobre");

        private static bool EsConsultaDefinicion(string n) =>
            ContieneAlguna(n, "significa", "define", "definicion", "que es");

        private static bool EsConsultaFecha(string n) =>
            ContieneAlguna(n, "cuando", "fecha", "en que ano");

        private static bool EsConsultaDiferenciaCiudadProvincia(string n) =>
            ContieneFrase(n, "diferencia") &&
            ContieneAlguna(n, "capital", "ciudad") &&
            ContieneFrase(n, "provincia");

        // ============================================================
        // ENTIDADES Y PALABRAS RELEVANTES
        // ============================================================

        private static List<string> ObtenerEntidadesConsulta(string n)
        {
            var entidades = new List<string>();

            foreach (var entidad in EntidadesOrdenadas)
            {
                if (!ContieneFrase(n, entidad))
                    continue;

                // Si ya se detectó una entidad más larga que la contiene, se omite
                // (evita contar dos veces "esteros del ibera" e "ibera").
                if (entidades.Any(e => ContieneFrase(e, entidad)))
                    continue;

                entidades.Add(entidad);
            }

            // "corrientes" es demasiado genérica cuando hay una entidad concreta.
            if (entidades.Count > 1)
                entidades.Remove("corrientes");

            return entidades;
        }

        private static List<string> ObtenerPalabrasRelevantes(string n) =>
            n.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => p.Length >= 3 && !PalabrasGenericas.Contains(p))
                .Distinct()
                .ToList();

        // ============================================================
        // ORACIONES
        // ============================================================

        private static string? ObtenerMejorOracion(
            string contenido,
            IReadOnlyCollection<string> palabras)
        {
            string? mejor = null;
            int mejorScore = 0;

            foreach (var oracion in SepararOraciones(contenido))
            {
                var tokens = new HashSet<string>(
                    NormalizarTexto(oracion).Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    StringComparer.Ordinal);

                int score = palabras.Count(tokens.Contains);

                if (score > mejorScore)
                {
                    mejorScore = score;
                    mejor = oracion;
                }
            }

            return mejor;
        }

        private static string PrimeraOracion(string? contenido) =>
            string.IsNullOrWhiteSpace(contenido)
                ? string.Empty
                : SepararOraciones(contenido).FirstOrDefault() ?? string.Empty;

        private static List<string> SepararOraciones(string? contenido)
        {
            if (string.IsNullOrWhiteSpace(contenido))
                return new List<string>();

            return RegexOraciones
                .Split(contenido)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
        }

        // ============================================================
        // FECHAS
        // ============================================================

        private static bool ContieneFecha(string? texto) =>
            !string.IsNullOrWhiteSpace(texto) && RegexAnio.IsMatch(texto);

        // ============================================================
        // MATCHING (sobre texto ya normalizado)
        // ============================================================

        private static bool ContieneAlguna(string textoNorm, params string[] frases)
        {
            foreach (var frase in frases)
            {
                if (ContieneFrase(textoNorm, frase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Indica si <paramref name="frase"/> aparece en <paramref name="textoNorm"/> como
        /// palabra o secuencia de palabras completas. Ambos deben estar normalizados.
        /// Reemplaza a ContienePalabra + ContieneEntidad + Contains por substring,
        /// sin re-normalizar ni re-dividir el texto en cada llamada.
        /// </summary>
        private static bool ContieneFrase(string textoNorm, string frase)
        {
            if (textoNorm.Length == 0 || frase.Length == 0)
                return false;

            int idx = 0;

            while ((idx = textoNorm.IndexOf(frase, idx, StringComparison.Ordinal)) >= 0)
            {
                int fin = idx + frase.Length;

                bool inicioOk = idx == 0 || textoNorm[idx - 1] == ' ';
                bool finOk = fin == textoNorm.Length || textoNorm[fin] == ' ';

                if (inicioOk && finOk)
                    return true;

                idx++;
            }

            return false;
        }

        // ============================================================
        // NORMALIZACIÓN
        // ============================================================

        private static string NormalizarTexto(string? texto)
        {
            if (string.IsNullOrWhiteSpace(texto))
                return string.Empty;

            var descompuesto = texto.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(descompuesto.Length);
            bool ultimoFueEspacio = true; // evita espacio inicial y colapsa repetidos

            foreach (var c in descompuesto)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                    continue;

                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                    ultimoFueEspacio = false;
                }
                else if (!ultimoFueEspacio)
                {
                    sb.Append(' ');
                    ultimoFueEspacio = true;
                }
            }

            if (sb.Length > 0 && sb[^1] == ' ')
                sb.Length--;

            return sb.ToString();
        }

        // ============================================================
        // CARGA E INDEXACIÓN (con caché opcional)
        // ============================================================

        private Task<IReadOnlyList<DocumentoIndexado>> ObtenerDocumentosAsync() =>
            ObtenerCacheadoAsync(CacheDocumentos, async () =>
            {
                var filas = await _db.CorpusDocumentos
                    .AsNoTracking()
                    .Select(d => new { d.Titulo, d.Contenido })
                    .ToListAsync();

                return filas
                    .Select(f => IndexarDocumento(f.Titulo ?? string.Empty, f.Contenido ?? string.Empty))
                    .ToList();
            });

        private Task<IReadOnlyList<DatoDuroIndexado>> ObtenerDatosDurosAsync() =>
            ObtenerCacheadoAsync(CacheDatosDuros, async () =>
            {
                var filas = await _db.DatosDuros
                    .AsNoTracking()
                    .Select(d => new { d.Clave, d.Valor })
                    .ToListAsync();

                return filas.Select(f =>
                {
                    var clave = f.Clave ?? string.Empty;
                    var valor = f.Valor ?? string.Empty;
                    var claveNorm = NormalizarTexto(clave);

                    return new DatoDuroIndexado(
                        clave,
                        valor,
                        claveNorm,
                        claveNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                        NormalizarTexto(valor));
                }).ToList();
            });

        private Task<IReadOnlyList<LugarIndexado>> ObtenerLugaresAsync() =>
            ObtenerCacheadoAsync(CacheLugares, async () =>
            {
                var filas = await _db.Lugares
                    .AsNoTracking()
                    .Select(l => new { l.Nombre, l.Descripcion, l.Localidad, l.Categoria })
                    .ToListAsync();

                return filas.Select(f => new LugarIndexado(
                    f.Nombre ?? string.Empty,
                    f.Descripcion ?? string.Empty,
                    NormalizarTexto(f.Nombre),
                    NormalizarTexto(f.Localidad),
                    NormalizarTexto(f.Categoria))).ToList();
            });

        private async Task<IReadOnlyList<T>> ObtenerCacheadoAsync<T>(
            string clave,
            Func<Task<List<T>>> cargar)
        {
            if (_cache != null &&
                _cache.TryGetValue(clave, out IReadOnlyList<T>? existente) &&
                existente != null)
            {
                return existente;
            }

            var datos = await cargar();

            // No se cachean listas vacías: así una tabla recién poblada se ve de inmediato.
            if (_cache != null && datos.Count > 0)
                _cache.Set(clave, (IReadOnlyList<T>)datos, CacheTtl);

            return datos;
        }

        private static DocumentoIndexado IndexarDocumento(string titulo, string contenido)
        {
            var tituloNorm = NormalizarTexto(titulo);
            var contenidoNorm = NormalizarTexto(contenido);

            return new DocumentoIndexado(
                titulo,
                contenido,
                tituloNorm,
                contenidoNorm,
                Tokenizar(tituloNorm),
                Tokenizar(contenidoNorm));
        }

        private static HashSet<string> Tokenizar(string textoNorm) =>
            new(textoNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
    }
}