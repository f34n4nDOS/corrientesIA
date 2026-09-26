using System.Globalization;
using System.Text;
using CorrientesIA.Data;
using Microsoft.EntityFrameworkCore;

namespace CorrientesIA.Api.Services;

// =============================================================
// CORRIENTESIA - GROUNDING SERVICE
// =============================================================
//
// Busca información verificada en MySQL antes de utilizar
// el modelo generativo.
//
// Objetivos:
// 1. Evitar falsos positivos.
// 2. Priorizar documentos específicos.
// 3. Detectar consultas de ubicación y definición.
// 4. Extraer una oración relevante del corpus.
// 5. Devolver null cuando el corpus realmente no sabe.
//
// =============================================================

public class GroundingService
{
    private readonly AppDbContext _db;

    public GroundingService(AppDbContext db)
    {
        _db = db;
    }

    // =============================================================
    // DATOS DUROS Y LUGARES
    // =============================================================

    public async Task<string?> BuscarDatoDuroAsync(string clave)
    {
        var dato = await _db.DatosDuros
            .FirstOrDefaultAsync(d => d.Clave == clave);

        return dato?.Valor;
    }

    public async Task<List<string>> BuscarLugaresAsync(string categoria)
    {
        return await _db.Lugares
            .Where(l => l.Categoria == categoria)
            .Select(l => l.Nombre)
            .ToListAsync();
    }

    // =============================================================
    // DATOS DUROS Y LUGARES POR PALABRAS CLAVE
    // =============================================================

    public async Task<string?> BuscarPorPalabrasClaveAsync(string mensaje)
    {
        var palabrasGenericas = ObtenerPalabrasGenericas();

        var palabras = ObtenerPalabrasNormalizadas(mensaje)
            .Where(p => p.Length >= 4)
            .Where(p => !palabrasGenericas.Contains(p))
            .ToHashSet();

        if (palabras.Count == 0)
            return null;

        // ---------------------------------------------------------
        // 1. Lugares almacenados directamente
        // ---------------------------------------------------------

        var lugares = await _db.Lugares.ToListAsync();

        foreach (var lugar in lugares)
        {
            var nombrePalabras =
                ObtenerPalabrasNormalizadas(lugar.Nombre)
                    .ToHashSet();

            var coincidencias =
                palabras.Count(p => nombrePalabras.Contains(p));

            if (coincidencias >=
                Math.Max(1, (int)Math.Ceiling(nombrePalabras.Count / 2.0)))
            {
                return $"{lugar.Nombre} — {lugar.Descripcion}";
            }
        }

        // ---------------------------------------------------------
        // 2. Datos duros
        // ---------------------------------------------------------

        var datos = await _db.DatosDuros.ToListAsync();

        foreach (var dato in datos)
        {
            var clavePalabras =
                ObtenerPalabrasNormalizadas(
                    dato.Clave.Replace('_', ' '))
                .ToHashSet();

            var coincidencias =
                palabras.Count(p => clavePalabras.Contains(p));

            if (coincidencias >= 2)
            {
                return $"{dato.Clave.Replace('_', ' ')}: {dato.Valor} (fuente: {dato.Fuente})";
            }
        }

        return null;
    }

    // =============================================================
    // BÚSQUEDA EN CORPUS
    // =============================================================

    public async Task<string?> BuscarEnCorpusAsync(string mensaje)
    {
        var palabras = ObtenerPalabrasNormalizadas(mensaje)
            .Where(p => p.Length >= 3)
            .ToHashSet();

        if (palabras.Count == 0)
            return null;

        var documentos =
            await _db.CorpusDocumentos.ToListAsync();

        if (documentos.Count == 0)
            return null;

        // ---------------------------------------------------------
        // INTENCIONES
        // ---------------------------------------------------------

        var palabrasUbicacion = new HashSet<string>
        {
            "donde",
            "queda",
            "ubicada",
            "ubicado",
            "localizada",
            "localizado",
            "situada",
            "situado",
            "encuentra"
        };

        var esConsultaUbicacion =
            palabras.Any(p => palabrasUbicacion.Contains(p));

        var palabrasDefinicion = new HashSet<string>
        {
            "que",
            "significa",
            "define",
            "definicion"
        };

        var esConsultaDefinicion =
            palabras.Any(p => palabrasDefinicion.Contains(p));

        // ---------------------------------------------------------
        // PALABRAS DE LA ENTIDAD
        // ---------------------------------------------------------

        var palabrasGenericas =
            ObtenerPalabrasGenericas();

        var palabrasEntidad = palabras
            .Where(p => !palabrasGenericas.Contains(p))
            .Where(p => p.Length >= 4)
            .ToHashSet();
            

        // ---------------------------------------------------------
        // CASO ESPECIAL:
        // "segunda ciudad más poblada"
        // ---------------------------------------------------------

        var consultaSegundaCiudad =
            palabras.Contains("segunda") &&
            (
                palabras.Contains("poblada") ||
                palabras.Contains("ciudad")
            );

        if (consultaSegundaCiudad)
        {
            var fraseClave =
                "segunda ciudad más poblada";

            var fraseNormalizada =
                NormalizarTexto(fraseClave);

            var documentoFrase =
                documentos.FirstOrDefault(d =>
                    NormalizarTexto(d.Contenido)
                        .Contains(
                            fraseNormalizada,
                            StringComparison.OrdinalIgnoreCase));

            if (documentoFrase is not null)
            {
                var contenidoNormalizado =
                    NormalizarTexto(documentoFrase.Contenido);

                var posicionFrase =
                    contenidoNormalizado.IndexOf(
                        fraseNormalizada,
                        StringComparison.OrdinalIgnoreCase);

                if (posicionFrase >= 0)
                {
                    var inicioFrase =
                        Math.Max(0, posicionFrase - 100);

                    var longitudFrase =
                        Math.Min(
                            700,
                            documentoFrase.Contenido.Length -
                            inicioFrase);

                    var contextoFrase =
                        documentoFrase.Contenido
                            .Substring(
                                inicioFrase,
                                longitudFrase)
                            .Trim();

                    return $"{documentoFrase.Titulo}: ...{contextoFrase}...";
                }
            }
        }

        // ---------------------------------------------------------
        // Si no tenemos entidad concreta, no debemos inventar
        // una coincidencia documental.
        // ---------------------------------------------------------

        if (palabrasEntidad.Count == 0)
            return null;

        // ---------------------------------------------------------
        // BUSCAR DOCUMENTOS
        // ---------------------------------------------------------

        var resultados = documentos
            .Select(documento =>
            {
                var tituloNormalizado =
                    NormalizarTexto(documento.Titulo);

                var contenidoNormalizado =
                    NormalizarTexto(documento.Contenido);

                var textoCompleto =
                    $"{tituloNormalizado} {contenidoNormalizado}";

                // -------------------------------------------------
                // Coincidencias generales
                // -------------------------------------------------

                var coincidencias =
                    palabras.Count(p =>
                        ContienePalabra(
                            textoCompleto,
                            p));

                // -------------------------------------------------
                // Coincidencias de entidad en título
                // -------------------------------------------------

                var coincidenciasTitulo =
                    palabrasEntidad.Count(p =>
                        ContienePalabra(
                            tituloNormalizado,
                            p));

                // -------------------------------------------------
                // Coincidencias de entidad en contenido
                // -------------------------------------------------

                var coincidenciasEntidad =
                    palabrasEntidad.Count(p =>
                        ContienePalabra(
                            contenidoNormalizado,
                            p));

                // -------------------------------------------------
                // Coincidencia fuerte
                // -------------------------------------------------

                var coincidenciaFuerte =
                    ContieneEntidad(
                        documento.Contenido,
                        palabrasEntidad);

                // -------------------------------------------------
                // Puntuación
                // -------------------------------------------------

                var puntuacion = 0;

                puntuacion +=
                    coincidencias;

                puntuacion +=
                    coincidenciasTitulo * 25;

                puntuacion +=
                    coincidenciasEntidad * 8;

                if (coincidenciaFuerte)
                    puntuacion += 50;

                // -------------------------------------------------
                // Coincidencias al comienzo del documento
                // -------------------------------------------------

                var inicioTexto =
                    contenidoNormalizado.Length > 1200
                        ? contenidoNormalizado[..1200]
                        : contenidoNormalizado;

                var coincidenciasInicio =
                    palabrasEntidad.Count(p =>
                        ContienePalabra(
                            inicioTexto,
                            p));

                puntuacion +=
                    coincidenciasInicio * 3;

                return new
                {
                    Documento = documento,
                    Puntuacion = puntuacion,
                    CoincidenciasEntidad =
                        coincidenciasEntidad,
                    CoincidenciasTitulo =
                        coincidenciasTitulo,
                    CoincidenciaFuerte =
                        coincidenciaFuerte
                };
            })
            .Where(x => x.Puntuacion > 0)
            .OrderByDescending(x => x.Puntuacion)
            .ThenByDescending(x => x.CoincidenciaFuerte)
            .ThenByDescending(x => x.CoincidenciasTitulo)
            .ThenByDescending(x => x.CoincidenciasEntidad)
            .ToList();

        var mejorResultado =
            resultados.FirstOrDefault();

        if (mejorResultado is null)
            return null;

        // =========================================================
        // VALIDACIÓN CONTRA FALSOS POSITIVOS
        // =========================================================
        //
        // Para una consulta de definición:
        //
        // "¿Qué es el Payé?"
        //
        // queremos un documento cuyo título sea Payé.
        //
        // Para:
        //
        // "¿Qué es el río Paraná?"
        //
        // queremos Río Paraná.
        //
        // Esto evita que "Argentina" haga ganar un documento
        // completamente diferente.
        // =========================================================

        if (esConsultaDefinicion)
        {
            if (mejorResultado.CoincidenciasTitulo == 0)
                return null;
        }

        // ---------------------------------------------------------
        // Para ubicación necesitamos que realmente exista
        // coincidencia con la entidad.
        // ---------------------------------------------------------

        if (esConsultaUbicacion)
        {
            if (mejorResultado.CoincidenciasEntidad == 0)
                return null;
        }

        // ---------------------------------------------------------
        // Para consultas generales exigimos una coincidencia
        // razonable con la entidad.
        // ---------------------------------------------------------

        if (!esConsultaDefinicion &&
            !esConsultaUbicacion)
        {
            if (mejorResultado.CoincidenciasEntidad == 0)
                return null;
        }

        var documentoEncontrado =
            mejorResultado.Documento;

        // =============================================================
        // CONSULTA DE UBICACIÓN
        // =============================================================

        if (esConsultaUbicacion)
        {
            return BuscarRespuestaUbicacion(
                documentoEncontrado,
                palabrasEntidad);
        }

        // =============================================================
        // CONSULTA DE DEFINICIÓN
        // =============================================================

        if (esConsultaDefinicion)
        {
            var posicionDefinicion =
                BuscarPosicionDefinicion(
                    documentoEncontrado,
                    palabras);

            // ---------------------------------------------------------
            // Si no existe una definición explícita, buscamos la
            // primera aparición de la entidad.
            // ---------------------------------------------------------

            if (posicionDefinicion < 0)
            {
                posicionDefinicion =
                    BuscarPosicionEntidad(
                        documentoEncontrado.Contenido,
                        palabrasEntidad);

                if (posicionDefinicion < 0)
                    return null;
            }

            var contextoDefinicion =
                ExtraerOracion(
                    documentoEncontrado.Contenido,
                    posicionDefinicion);

            if (string.IsNullOrWhiteSpace(
                contextoDefinicion))
            {
                return null;
            }

            return
                $"{documentoEncontrado.Titulo}: " +
                contextoDefinicion;
        }

        // =============================================================
        // CONSULTA GENERAL
        // =============================================================

        var posicion =
            BuscarPosicionEntidad(
                documentoEncontrado.Contenido,
                palabrasEntidad);

        if (posicion < 0)
        {
            posicion =
                BuscarPosicionPalabraRelevante(
                    documentoEncontrado.Contenido,
                    palabras);
        }

        if (posicion < 0)
            posicion = 0;

        var contexto =
            ExtraerOracion(
                documentoEncontrado.Contenido,
                posicion);

        if (string.IsNullOrWhiteSpace(contexto))
            return null;

        return
            $"{documentoEncontrado.Titulo}: " +
            contexto;
    }

    // =============================================================
    // RESPUESTA DE UBICACIÓN
    // =============================================================

    private static string? BuscarRespuestaUbicacion(
        CorrientesIA.Data.Models.CorpusDocumento documento,
        HashSet<string> palabrasEntidad)
    {
        if (palabrasEntidad.Count == 0)
            return null;

        var contenido =
            documento.Contenido;

        if (string.IsNullOrWhiteSpace(contenido))
            return null;

        var oraciones =
            contenido
                .Split(
                    new[] { '.', '\n' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(o => o.Trim())
                .Where(o =>
                    !string.IsNullOrWhiteSpace(o))
                .ToList();
               

        var indicadoresUbicacion = new[]
        {
            "ubicada",
            "ubicado",
            "localizada",
            "localizado",
            "situada",
            "situado",
            "se encuentra",
            "queda en",
            "está en",
            "esta en",
            "está ubicada",
            "esta ubicada",
            "está situado",
            "esta situado",
            "pertenece a",
            "perteneciente a",
            "a orillas de",
            "distante de",
            "a kilómetros de",
            "a kilometros de"
        };

        for (int i = 0;
             i < oraciones.Count;
             i++)
        {
            var oracionNormalizada =
                NormalizarTexto(oraciones[i]);

            var contieneEntidad =
                palabrasEntidad.All(p =>
                    ContienePalabra(
                        oracionNormalizada,
                        p));

            if (!contieneEntidad)
                continue;

            // ---------------------------------------------------------
            // Primera opción:
            // la misma oración contiene información de ubicación.
            // ---------------------------------------------------------

            var indicadoresGeograficos = new[]
{
    "provincia de",
    "provincia del",
    "provincia",
    "argentina",
    "nordeste",
    "noroeste",
    "sudeste",
    "sudoeste",
    "norte",
    "sur",
    "este",
    "oeste",
    "región",
    "region",
    "departamento",
    "municipio",
    "kilometros de",
    "km de"
};

var contieneUbicacion =
    indicadoresUbicacion.Any(indicador =>
        oracionNormalizada.Contains(
            NormalizarTexto(indicador),
            StringComparison.OrdinalIgnoreCase))
    ||
    indicadoresGeograficos.Any(indicador =>
        oracionNormalizada.Contains(
            NormalizarTexto(indicador),
            StringComparison.OrdinalIgnoreCase));

            if (contieneUbicacion)
            {
                return
                    $"{documento.Titulo}: " +
                    oraciones[i];
            }

            // ---------------------------------------------------------
            // Segunda opción:
            // la siguiente oración contiene información de ubicación.
            // ---------------------------------------------------------

            if (i + 1 < oraciones.Count)
            {
                var siguienteNormalizada =
                    NormalizarTexto(
                        oraciones[i + 1]);

                var siguienteEsUbicacion =
                    indicadoresUbicacion.Any(indicador =>
                        siguienteNormalizada.Contains(
                            NormalizarTexto(indicador),
                            StringComparison.OrdinalIgnoreCase));

                if (siguienteEsUbicacion)
                {
                    return
                        $"{documento.Titulo}: " +
                        oraciones[i + 1];
                }
            }
        }

        // ---------------------------------------------------------
        // Fallback para lugares mencionados sin ubicación concreta.
        // ---------------------------------------------------------

        var entidad =
            ObtenerNombreEntidad(
                documento,
                palabrasEntidad);

        if (!string.IsNullOrWhiteSpace(entidad))
        {
            return
                $"{entidad} aparece mencionado como una localidad " +
                "de la provincia de Corrientes, pero no hay " +
                "información suficiente en la base para indicar " +
                "su ubicación exacta.";
        }

        return null;
    }

    // =============================================================
    // IDENTIFICAR ENTIDAD
    // =============================================================

    private static bool ContieneEntidad(
        string contenido,
        HashSet<string> palabrasEntidad)
    {
        if (palabrasEntidad.Count == 0)
            return false;

        var contenidoNormalizado =
            NormalizarTexto(contenido);

        // ---------------------------------------------------------
        // Entidad de una sola palabra.
        // ---------------------------------------------------------

        if (palabrasEntidad.Count == 1)
        {
            var palabra =
                palabrasEntidad.First();

            return ContienePalabra(
                contenidoNormalizado,
                palabra);
        }

        // ---------------------------------------------------------
        // Entidades de varias palabras.
        // ---------------------------------------------------------

        var posiciones =
            palabrasEntidad
                .Select(p => new
                {
                    Palabra = p,
                    Posicion =
                        BuscarPalabra(
                            contenidoNormalizado,
                            p)
                })
                .Where(x =>
                    x.Posicion >= 0)
                .OrderBy(x =>
                    x.Posicion)
                .ToList();

        if (posiciones.Count !=
            palabrasEntidad.Count)
        {
            return false;
        }

        var distancia =
            posiciones.Last().Posicion -
            posiciones.First().Posicion;

        return distancia <= 80;
    }

    // =============================================================
    // POSICIÓN DE ENTIDAD
    // =============================================================

    private static int BuscarPosicionEntidad(
        string contenido,
        HashSet<string> palabrasEntidad)
    {
        if (palabrasEntidad.Count == 0)
            return -1;

        var contenidoNormalizado =
            NormalizarTexto(contenido);

        var palabrasOrdenadas =
            palabrasEntidad
                .OrderByDescending(p =>
                    p.Length)
                .ToList();

        foreach (var palabra in palabrasOrdenadas)
        {
            var posicion =
                BuscarPalabra(
                    contenidoNormalizado,
                    palabra);

            if (posicion >= 0)
                return posicion;
        }

        return -1;
    }

    // =============================================================
    // OBTENER NOMBRE DE ENTIDAD
    // =============================================================

    private static string ObtenerNombreEntidad(
        CorrientesIA.Data.Models.CorpusDocumento documento,
        HashSet<string> palabrasEntidad)
    {
        var palabrasTitulo =
            ObtenerPalabrasNormalizadas(
                documento.Titulo);

        var coincidencias =
            palabrasTitulo
                .Where(p =>
                    palabrasEntidad.Contains(p))
                .OrderByDescending(p =>
                    p.Length)
                .ToList();

        if (coincidencias.Count > 0)
            return documento.Titulo;

        return string.Join(
            " ",
            palabrasEntidad
                .OrderByDescending(p =>
                    p.Length));
    }

    // =============================================================
    // DEFINICIONES
    // =============================================================

    private static int BuscarPosicionDefinicion(
        CorrientesIA.Data.Models.CorpusDocumento documento,
        HashSet<string> palabrasConsulta)
    {
        var contenidoNormalizado =
            NormalizarTexto(
                documento.Contenido);

        var tituloNormalizado =
            NormalizarTexto(
                documento.Titulo);

        var palabrasGenericas =
            new HashSet<string>
            {
                "que",
                "significa",
                "define",
                "definicion",
                "es",
                "son",
                "el",
                "la",
                "los",
                "las",
                "un",
                "una",
                "del",
                "de",
                "se",
                "como"
            };

        // ---------------------------------------------------------
        // Primero priorizamos palabras que aparecen en el título.
        // ---------------------------------------------------------

        var palabrasTema =
            palabrasConsulta
                .Where(p =>
                    !palabrasGenericas.Contains(p))
                .Where(p =>
                    p.Length >= 4)
                .Where(p =>
                    ContienePalabra(
                        tituloNormalizado,
                        p))
                .OrderByDescending(p =>
                    p.Length)
                .ToList();

        // ---------------------------------------------------------
        // Si no encontramos coincidencia con el título,
        // usamos las palabras de tema disponibles.
        // ---------------------------------------------------------

        if (palabrasTema.Count == 0)
        {
            palabrasTema =
                palabrasConsulta
                    .Where(p =>
                        !palabrasGenericas.Contains(p))
                    .Where(p =>
                        p.Length >= 4)
                    .OrderByDescending(p =>
                        p.Length)
                    .ToList();
        }

        foreach (var palabraTema in palabrasTema)
        {
            var patrones =
                new[]
                {
                    $"{palabraTema} es ",
                    $"{palabraTema} es una ",
                    $"{palabraTema} es un ",
                    $"{palabraTema} significa ",
                    $"{palabraTema} consiste en ",
                    $"{palabraTema} son "
                };

            foreach (var patron in patrones)
            {
                var posicion =
                    contenidoNormalizado.IndexOf(
                        patron,
                        StringComparison.OrdinalIgnoreCase);

                if (posicion >= 0)
                    return posicion;
            }
        }

        return -1;
    }

    // =============================================================
    // BÚSQUEDA GENERAL
    // =============================================================

    private static int BuscarPosicionPalabraRelevante(
        string contenido,
        HashSet<string> palabras)
    {
        if (palabras.Count == 0)
            return -1;

        var palabrasContenido =
            ObtenerPalabrasNormalizadas(
                contenido);

        var palabraMasRelevante =
            palabras
                .OrderByDescending(p =>
                    palabrasContenido.Count(
                        x => x == p))
                .FirstOrDefault();

        if (palabraMasRelevante is null)
            return -1;

        return BuscarPalabra(
            NormalizarTexto(contenido),
            palabraMasRelevante);
    }

    // =============================================================
    // EXTRACCIÓN DE ORACIÓN
    // =============================================================

    private static string ExtraerOracion(
        string contenido,
        int posicion)
    {
        if (string.IsNullOrWhiteSpace(contenido))
            return string.Empty;

        posicion =
            Math.Clamp(
                posicion,
                0,
                contenido.Length - 1);

        var inicioOracion =
            BuscarInicioBloque(
                contenido,
                posicion);

        var finOracion =
            BuscarFinBloque(
                contenido,
                posicion);

        if (finOracion <= inicioOracion)
            return string.Empty;

        var longitud =
            Math.Min(
                700,
                finOracion - inicioOracion);

        return contenido
            .Substring(
                inicioOracion,
                longitud)
            .Trim();
    }

    private static int BuscarInicioBloque(
        string contenido,
        int posicion)
    {
        var ultimoPunto =
            contenido.LastIndexOf(
                '.',
                posicion);

        var ultimoSalto =
            contenido.LastIndexOf(
                '\n',
                posicion);

        var inicio =
            Math.Max(
                ultimoPunto,
                ultimoSalto);

        inicio++;

        while (
            inicio < contenido.Length &&
            char.IsWhiteSpace(
                contenido[inicio]))
        {
            inicio++;
        }

        return inicio;
    }

    private static int BuscarFinBloque(
        string contenido,
        int posicion)
    {
        var siguientePunto =
            contenido.IndexOf(
                '.',
                posicion);

        var siguienteSalto =
            contenido.IndexOf(
                '\n',
                posicion);

        if (siguientePunto < 0)
            siguientePunto =
                contenido.Length;

        if (siguienteSalto >= 0)
        {
            return Math.Min(
                siguientePunto,
                siguienteSalto);
        }

        return siguientePunto;
    }

    // =============================================================
    // NORMALIZACIÓN
    // =============================================================

    private static HashSet<string>
        ObtenerPalabrasNormalizadas(
            string texto)
    {
        return NormalizarTexto(texto)
            .Split(
                new[]
                {
                    ' ',
                    '\n',
                    '\r',
                    '\t',
                    ',',
                    '.',
                    '?',
                    '!',
                    ';',
                    ':',
                    '¿',
                    '¡',
                    '(',
                    ')',
                    '"',
                    '\'',
                    '-'
                },
                StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();
    }

    private static string NormalizarTexto(
        string texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
            return string.Empty;

        var normalizado =
            texto.Normalize(
                NormalizationForm.FormD);

        var caracteres =
            normalizado
                .Where(c =>
                    CharUnicodeInfo.GetUnicodeCategory(c) !=
                    UnicodeCategory.NonSpacingMark)
                .ToArray();

        return new string(caracteres)
            .Normalize(
                NormalizationForm.FormC)
            .ToLowerInvariant();
    }

    // =============================================================
    // BÚSQUEDA DE PALABRA EXACTA
    // =============================================================

    private static bool ContienePalabra(
        string texto,
        string palabra)
    {
        return BuscarPalabra(
            texto,
            palabra) >= 0;
    }

    private static int BuscarPalabra(
        string texto,
        string palabra)
    {
        if (string.IsNullOrWhiteSpace(texto) ||
            string.IsNullOrWhiteSpace(palabra))
        {
            return -1;
        }

        var inicio = 0;

        while (inicio < texto.Length)
        {
            var posicion =
                texto.IndexOf(
                    palabra,
                    inicio,
                    StringComparison.OrdinalIgnoreCase);

            if (posicion < 0)
                return -1;

            var antesValido =
                posicion == 0 ||
                !char.IsLetterOrDigit(
                    texto[posicion - 1]);

            var fin =
                posicion + palabra.Length;

            var despuesValido =
                fin >= texto.Length ||
                !char.IsLetterOrDigit(
                    texto[fin]);

            if (antesValido &&
                despuesValido)
            {
                return posicion;
            }

            inicio =
                posicion + palabra.Length;
        }

        return -1;
    }

    // =============================================================
    // PALABRAS GENÉRICAS
    // =============================================================

    private static HashSet<string>
        ObtenerPalabrasGenericas()
    {
        return new HashSet<string>
        {
            "ciudad",
            "provincia",
            "cerca",
            "capital",
            "corrientes",
            "lugares",
            "lugar",
            "turisticos",
            "turistico",
            "turisticas",
            "turistica",
            "habitantes",
            "fecha",
            "cuando",
            "donde",
            "cual",
            "cuales",
            "que",
            "es",
            "son",
            "del",
            "los",
            "las",
            "una",
            "uno",
            "unos",
            "unas",
            "para",
            "como",
            "queda",
            "ubicada",
            "ubicado",
            "localizada",
            "localizado",
            "significa",
            "define",
            "definicion",
            "esta",
            "primer",
            "primera",
            "segundo",
            "segunda",
            "presidente",
            "presidentes",
            "quien",
            "argentina",
        };
    }
}