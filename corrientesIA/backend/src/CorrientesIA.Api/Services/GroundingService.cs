using System.Globalization;
using System.Text;
using CorrientesIA.Data;
using Microsoft.EntityFrameworkCore;

namespace CorrientesIA.Api.Services;

// Capa de grounding:
// busca información verificada en MySQL antes de utilizar
// el modelo generativo, reduciendo respuestas inventadas.
public class GroundingService
{
    private readonly AppDbContext _db;

    public GroundingService(AppDbContext db)
    {
        _db = db;
    }

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
    // DATOS DUROS Y LUGARES
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
            var nombrePalabras = ObtenerPalabrasNormalizadas(lugar.Nombre)
                .ToHashSet();

            // Exigimos coincidencia con el nombre del lugar.
            if (palabras.Count(p => nombrePalabras.Contains(p)) >=
                Math.Max(1, nombrePalabras.Count / 2))
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
            var clavePalabras = ObtenerPalabrasNormalizadas(
                    dato.Clave.Replace('_', ' '))
                .ToHashSet();

            // Se mantienen al menos dos coincidencias para evitar
            // falsos positivos.
            if (palabras.Count(p => clavePalabras.Contains(p)) >= 2)
            {
                return $"{dato.Clave.Replace('_', ' ')}: {dato.Valor} (fuente: {dato.Fuente})";
            }
        }

        return null;
    }

    // =============================================================
    // BÚSQUEDA EN INFORMACIÓN DOCUMENTAL
    // =============================================================

    public async Task<string?> BuscarEnCorpusAsync(string mensaje)
    {
        var palabras = ObtenerPalabrasNormalizadas(mensaje)
            .Where(p => p.Length >= 3)
            .ToHashSet();

        if (palabras.Count == 0)
            return null;

        var documentos = await _db.CorpusDocumentos.ToListAsync();

Console.WriteLine("===== DEBUG CORPUS =====");
Console.WriteLine($"Total documentos: {documentos.Count}");

foreach (var doc in documentos.Where(d =>
    NormalizarTexto(d.Contenido).Contains("san luis") ||
    NormalizarTexto(d.Contenido).Contains("palmar")))
{
    Console.WriteLine($"ID: {doc.Id}");
    Console.WriteLine($"TITULO: {doc.Titulo}");
    Console.WriteLine($"CONTENIDO: {doc.Contenido[..Math.Min(500, doc.Contenido.Length)]}");
    Console.WriteLine("========================");
}

        // ---------------------------------------------------------
        // Detectar intención
        // ---------------------------------------------------------

        var palabrasUbicacion = new HashSet<string>
        {
            "donde",
            "queda",
            "ubicada",
            "ubicado",
            "localizada",
            "localizado",
            "se encuentra a",
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
        // Palabras que probablemente representan la entidad
        // consultada.
        //
        // Ejemplo:
        // "donde queda San Luis del Palmar?"
        //
        // entidad:
        // san + luis + palmar
        // ---------------------------------------------------------

        var palabrasGenericas = ObtenerPalabrasGenericas();

        var palabrasEntidad = palabras
            .Where(p => !palabrasGenericas.Contains(p))
            .Where(p => p.Length >= 4)
            .ToHashSet();

        // ---------------------------------------------------------
        // Caso especial:
        // "segunda ciudad más poblada"
        // ---------------------------------------------------------

        var consultaSegundaCiudad =
            palabras.Contains("segunda") &&
            (palabras.Contains("poblada") ||
             palabras.Contains("ciudad"));

        if (consultaSegundaCiudad)
        {
            var fraseClave = "segunda ciudad más poblada";

            var fraseNormalizada = NormalizarTexto(fraseClave);

            var documentoFrase = documentos.FirstOrDefault(d =>
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
                    var inicioFrase = Math.Max(
                        0,
                        posicionFrase - 100);

                    var longitudFrase = Math.Min(
                        700,
                        documentoFrase.Contenido.Length - inicioFrase);

                    var contextoFrase = documentoFrase.Contenido
                        .Substring(inicioFrase, longitudFrase)
                        .Trim();

                    return $"{documentoFrase.Titulo}: ...{contextoFrase}...";
                }
            }
        }

        // ---------------------------------------------------------
        // Buscar documentos
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

                // Coincidencias generales.
                var coincidencias = palabras.Count(
                    p => textoCompleto.Contains(
                        p,
                        StringComparison.OrdinalIgnoreCase));

                // Coincidencias del título.
                var coincidenciasTitulo = palabrasEntidad.Count(
                    p => tituloNormalizado.Contains(
                        p,
                        StringComparison.OrdinalIgnoreCase));

                // Coincidencias específicas de la entidad.
                var coincidenciasEntidad = palabrasEntidad.Count(
                    p => contenidoNormalizado.Contains(
                        p,
                        StringComparison.OrdinalIgnoreCase));

                // Aparece el nombre completo de la entidad
                // si reconstruimos las palabras principales.
                var coincidenciaFuerte = ContieneEntidad(
                    documento.Contenido,
                    palabrasEntidad);

                var puntuacion = 0;

                // Coincidencias generales tienen poco peso.
                puntuacion += coincidencias;

                // El título tiene bastante más peso.
                puntuacion += coincidenciasTitulo * 15;

                // Las palabras de la entidad son importantes.
                puntuacion += coincidenciasEntidad * 8;

                // Una coincidencia fuerte del nombre completo
                // tiene muchísimo peso.
                if (coincidenciaFuerte)
                    puntuacion += 50;

                // Priorizamos coincidencias cercanas al principio.
                var inicioTexto = contenidoNormalizado.Length > 1200
                    ? contenidoNormalizado[..1200]
                    : contenidoNormalizado;

                var coincidenciasInicio = palabrasEntidad.Count(
                    p => inicioTexto.Contains(
                        p,
                        StringComparison.OrdinalIgnoreCase));

                puntuacion += coincidenciasInicio * 3;

                return new
                {
                    Documento = documento,
                    Puntuacion = puntuacion,
                    CoincidenciasEntidad = coincidenciasEntidad,
                    CoincidenciasTitulo = coincidenciasTitulo,
                    CoincidenciaFuerte = coincidenciaFuerte
                };
            })
            .Where(x => x.Puntuacion > 0)
            .OrderByDescending(x => x.Puntuacion)
            .ThenByDescending(x => x.CoincidenciaFuerte)
            .ThenByDescending(x => x.CoincidenciasEntidad)
            .ThenByDescending(x => x.CoincidenciasTitulo)
            .ToList();

        var mejorResultado = resultados.FirstOrDefault();

        if (mejorResultado is null)
            return null;

        var documentoEncontrado =
            mejorResultado.Documento;

        // ---------------------------------------------------------
        // CONSULTA DE UBICACIÓN
        // ---------------------------------------------------------

        if (esConsultaUbicacion)
        {
            return BuscarRespuestaUbicacion(
                documentoEncontrado,
                palabrasEntidad);
        }

        // ---------------------------------------------------------
        // CONSULTA DE DEFINICIÓN
        // ---------------------------------------------------------

        if (esConsultaDefinicion)
        {
            var posicionDefinicion =
                BuscarPosicionDefinicion(
                    documentoEncontrado,
                    palabras);

            if (posicionDefinicion < 0)
                return null;

            var contextoDefinicion =
                ExtraerOracion(
                    documentoEncontrado.Contenido,
                    posicionDefinicion);

            if (string.IsNullOrWhiteSpace(contextoDefinicion))
                return null;

            return $"{documentoEncontrado.Titulo}: {contextoDefinicion}";
        }

        // ---------------------------------------------------------
        // CONSULTA GENERAL
        // ---------------------------------------------------------

        var posicion =
            BuscarPosicionEntidad(
                documentoEncontrado.Contenido,
                palabrasEntidad);

        if (posicion < 0)
        {
            posicion = BuscarPosicionPalabraRelevante(
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

        return $"{documentoEncontrado.Titulo}: {contexto}";
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

    var contenido = documento.Contenido;

    Console.WriteLine("===== DEBUG UBICACION =====");
Console.WriteLine($"Entidad: {string.Join(", ", palabrasEntidad)}");
Console.WriteLine($"Contenido: {contenido}");
    if (string.IsNullOrWhiteSpace(contenido))
        return null;

    // ---------------------------------------------------------
    // Buscar oraciones del documento que contengan información
    // explícita de ubicación.
    // ---------------------------------------------------------

    var oraciones = contenido
        .Split(
            new[] { '.', '\n' },
            StringSplitOptions.RemoveEmptyEntries)
        .Select(o => o.Trim())
        .Where(o => !string.IsNullOrWhiteSpace(o))
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

    foreach (var oracion in oraciones)
    {
        var oracionNormalizada =
            NormalizarTexto(oracion);

        // Todas las palabras importantes de la entidad
        // deben aparecer en la misma oración.
        var contieneEntidad =
            palabrasEntidad.All(p =>
                oracionNormalizada.Contains(
                    p,
                    StringComparison.OrdinalIgnoreCase));

        if (!contieneEntidad)
            continue;

        var contieneUbicacion =
            indicadoresUbicacion.Any(indicador =>
                oracionNormalizada.Contains(
                    NormalizarTexto(indicador),
                    StringComparison.OrdinalIgnoreCase));

        if (contieneUbicacion)
        {
            return $"{documento.Titulo}: {oracion}";
        }
    }

    // ---------------------------------------------------------
    // Si no encontramos una oración explícitamente geográfica,
    // mantenemos el comportamiento seguro anterior.
    // ---------------------------------------------------------

    var entidad = ObtenerNombreEntidad(
        documento,
        palabrasEntidad);

    if (!string.IsNullOrWhiteSpace(entidad))
    {
        return $"{entidad} aparece mencionado como una localidad de la provincia de Corrientes, pero no hay información suficiente en la base para indicar su ubicación exacta.";
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

    // Para entidades de una sola palabra.
    if (palabrasEntidad.Count == 1)
    {
        var palabra = palabrasEntidad.First();

        return contenidoNormalizado.Contains(
            palabra,
            StringComparison.OrdinalIgnoreCase);
    }

    // Para entidades de varias palabras necesitamos que
    // aparezcan todas y estén próximas entre sí.
    var posiciones = palabrasEntidad
        .Select(p => new
        {
            Palabra = p,
            Posicion = contenidoNormalizado.IndexOf(
                p,
                StringComparison.OrdinalIgnoreCase)
        })
        .Where(x => x.Posicion >= 0)
        .OrderBy(x => x.Posicion)
        .ToList();

    // Si falta alguna palabra, no es una coincidencia fuerte.
    if (posiciones.Count != palabrasEntidad.Count)
        return false;

    var distancia =
        posiciones.Last().Posicion -
        posiciones.First().Posicion;

    // Para "san luis palmar", por ejemplo, las palabras
    // deben encontrarse realmente juntas en el texto.
    return distancia <= 80;
}

    private static int BuscarPosicionEntidad(
        string contenido,
        HashSet<string> palabrasEntidad)
    {
        if (palabrasEntidad.Count == 0)
            return -1;

        var contenidoNormalizado =
            NormalizarTexto(contenido);

        // Intentamos encontrar primero la palabra más larga,
        // que normalmente identifica mejor a la entidad.
        var palabrasOrdenadas = palabrasEntidad
            .OrderByDescending(p => p.Length)
            .ToList();

        foreach (var palabra in palabrasOrdenadas)
        {
            var posicion =
                contenidoNormalizado.IndexOf(
                    palabra,
                    StringComparison.OrdinalIgnoreCase);

            if (posicion >= 0)
                return posicion;
        }

        return -1;
    }

    private static string ObtenerNombreEntidad(
        CorrientesIA.Data.Models.CorpusDocumento documento,
        HashSet<string> palabrasEntidad)
    {
        var tituloNormalizado =
            NormalizarTexto(documento.Titulo);

        var palabrasTitulo =
            ObtenerPalabrasNormalizadas(
                documento.Titulo);

        var coincidencias =
            palabrasTitulo
                .Where(p => palabrasEntidad.Contains(p))
                .OrderByDescending(p => p.Length)
                .ToList();

        if (coincidencias.Count > 0)
        {
            return documento.Titulo;
        }

        return string.Join(
            " ",
            palabrasEntidad.OrderByDescending(
                p => p.Length));
    }

    // =============================================================
    // DEFINICIONES
    // =============================================================

    private static int BuscarPosicionDefinicion(
        CorrientesIA.Data.Models.CorpusDocumento documento,
        HashSet<string> palabrasConsulta)
    {
        var contenidoNormalizado =
            NormalizarTexto(documento.Contenido);

        var tituloNormalizado =
            NormalizarTexto(documento.Titulo);

        var palabrasGenericas = new HashSet<string>
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

        var palabrasTema = palabrasConsulta
            .Where(p =>
                !palabrasGenericas.Contains(p) &&
                p.Length >= 4)
            .Where(p =>
                tituloNormalizado.Contains(
                    p,
                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Length)
            .ToList();

        if (palabrasTema.Count == 0)
        {
            palabrasTema = palabrasConsulta
                .Where(p =>
                    !palabrasGenericas.Contains(p) &&
                    p.Length >= 4)
                .OrderByDescending(p => p.Length)
                .ToList();
        }

        foreach (var palabraTema in palabrasTema)
        {
            var patrones = new[]
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

        var palabrasContenido = contenido
            .Split(
                new[]
                {
                    ' ',
                    '\n',
                    '\r',
                    '.',
                    ',',
                    ';',
                    ':'
                },
                StringSplitOptions.RemoveEmptyEntries);

        var palabraMasRelevante = palabras
            .OrderByDescending(p =>
                palabrasContenido.Count(x =>
                    NormalizarTexto(x)
                        .Equals(
                            p,
                            StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault();

        if (palabraMasRelevante is null)
            return -1;

        var contenidoNormalizado =
            NormalizarTexto(contenido);

        return contenidoNormalizado.IndexOf(
            palabraMasRelevante,
            StringComparison.OrdinalIgnoreCase);
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

        posicion = Math.Clamp(
            posicion,
            0,
            contenido.Length - 1);

        var inicioOracion =
            BuscarInicioBloque(contenido, posicion);

        var finOracion =
            BuscarFinBloque(contenido, posicion);

        if (finOracion <= inicioOracion)
            return string.Empty;

        var longitud = Math.Min(
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
        var ultimoPunto = contenido.LastIndexOf(
            '.',
            posicion);

        var ultimoSalto = contenido.LastIndexOf(
            '\n',
            posicion);

        var inicio = Math.Max(
            ultimoPunto,
            ultimoSalto);

        inicio++;

        while (
            inicio < contenido.Length &&
            char.IsWhiteSpace(contenido[inicio]))
        {
            inicio++;
        }

        return inicio;
    }

    private static int BuscarFinBloque(
        string contenido,
        int posicion)
    {
        var siguientePunto = contenido.IndexOf(
            '.',
            posicion);

        var siguienteSalto = contenido.IndexOf(
            '\n',
            posicion);

        if (siguientePunto < 0)
            siguientePunto = contenido.Length;

        if (siguienteSalto >= 0)
            return Math.Min(
                siguientePunto,
                siguienteSalto);

        return siguientePunto;
    }

    // =============================================================
    // NORMALIZACIÓN
    // =============================================================

    private static HashSet<string> ObtenerPalabrasNormalizadas(
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

    private static string NormalizarTexto(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
            return string.Empty;

        var normalizado =
            texto.Normalize(
                NormalizationForm.FormD);

        var caracteres = normalizado
            .Where(c =>
                CharUnicodeInfo.GetUnicodeCategory(c) !=
                UnicodeCategory.NonSpacingMark)
            .ToArray();

        return new string(caracteres)
            .Normalize(NormalizationForm.FormC)
            .ToLowerInvariant();
    }

    private static HashSet<string> ObtenerPalabrasGenericas()
    {
        return new HashSet<string>
        {
            "ciudad",
            "provincia",
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
            "qué",
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
            "definicion"
        };
    }
}