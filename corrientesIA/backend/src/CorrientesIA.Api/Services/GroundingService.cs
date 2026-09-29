using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CorrientesIA.Data;
using CorrientesIA.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CorrientesIA.Api.Services
{
    public class GroundingService
    {
        private readonly AppDbContext _db;

        public GroundingService(AppDbContext db)
        {
            _db = db;
        }

        // ============================================================
        // DATOS DUROS
        // ============================================================

        public async Task<string?> BuscarDatoDuroAsync(string consulta)
        {
            if (string.IsNullOrWhiteSpace(consulta))
                return null;

            var consultaNormalizada = NormalizarTexto(consulta);

            var datos = await _db.DatosDuros
                .AsNoTracking()
                .ToListAsync();

            if (datos.Count == 0)
                return null;

            foreach (var dato in datos)
            {
                var clave = NormalizarTexto(dato.Clave);
                var valor = NormalizarTexto(dato.Valor);

                if (string.IsNullOrWhiteSpace(clave))
                    continue;

                var palabrasClave = clave
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);

                var coincidencias = palabrasClave.Count(
                    palabra => ContienePalabra(
                        consultaNormalizada,
                        palabra));

                if (coincidencias == palabrasClave.Length)
                {
                    return $"{dato.Clave}: {dato.Valor}";
                }

                if (!string.IsNullOrWhiteSpace(valor) &&
                    consultaNormalizada.Contains(
                        valor,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return $"{dato.Clave}: {dato.Valor}";
                }
            }

            return null;
        }

        // ============================================================
        // LUGARES
        // ============================================================

        public async Task<string?> BuscarLugaresAsync(string consulta)
        {
            if (string.IsNullOrWhiteSpace(consulta))
                return null;

            var normalizada = NormalizarTexto(consulta);

            var lugares = await _db.Lugares
                .AsNoTracking()
                .ToListAsync();

            if (lugares.Count == 0)
                return null;

            foreach (var lugar in lugares)
            {
                var nombre = NormalizarTexto(lugar.Nombre);
                var descripcion = lugar.Descripcion ?? string.Empty;
                var localidad = NormalizarTexto(lugar.Localidad);
                var categoria = NormalizarTexto(lugar.Categoria);

                if (string.IsNullOrWhiteSpace(nombre))
                    continue;

                bool coincideNombre =
                    ContieneEntidad(normalizada, nombre);

                bool coincideLocalidad =
                    !string.IsNullOrWhiteSpace(localidad) &&
                    ContieneEntidad(normalizada, localidad);

                bool coincideCategoria =
                    !string.IsNullOrWhiteSpace(categoria) &&
                    ContienePalabra(normalizada, categoria);

                if (coincideNombre ||
                    (coincideLocalidad && coincideCategoria))
                {
                    return $"{lugar.Nombre}: {descripcion}";
                }
            }

            return null;
        }

        // ============================================================
        // BÚSQUEDA POR PALABRAS CLAVE
        // ============================================================

        public async Task<string?> BuscarPorPalabrasClaveAsync(
            string consulta)
        {
            if (string.IsNullOrWhiteSpace(consulta))
                return null;

            // --------------------------------------------------------
            // Actualidad política:
            // nunca devolver información histórica del corpus.
            // --------------------------------------------------------

            if (EsConsultaGobernador(consulta) &&
                EsConsultaActualidad(consulta))
            {
                return null;
            }

            // --------------------------------------------------------
            // Consultas meta
            // --------------------------------------------------------

            if (EsConsultaFaltante(consulta))
            {
                return await ConstruirRespuestaSobreFaltantesAsync();
            }

            if (EsConsultaConocimiento(consulta))
            {
                return await ConstruirRespuestaSobreConocimientoAsync();
            }

            // --------------------------------------------------------
            // Datos estructurados
            // --------------------------------------------------------

            var datoDuro = await BuscarDatoDuroAsync(consulta);

            if (!string.IsNullOrWhiteSpace(datoDuro))
                return datoDuro;

            // --------------------------------------------------------
            // Lugares.
            //
            // Solo se utiliza para una consulta claramente referida
            // a un lugar concreto.
            // --------------------------------------------------------

            bool consultaLugar =
                EsConsultaUbicacion(consulta) ||
                EsConsultaInformacionLugar(consulta);

            if (consultaLugar)
            {
                var lugar = await BuscarLugaresAsync(consulta);

                if (!string.IsNullOrWhiteSpace(lugar))
                    return lugar;
            }

            return null;
        }

        // ============================================================
        // CORPUS PRINCIPAL
        // ============================================================

        public async Task<string?> BuscarEnCorpusAsync(string consulta)
        {
            if (string.IsNullOrWhiteSpace(consulta))
                return null;

            var normalizada = NormalizarTexto(consulta);

            // --------------------------------------------------------
            // INTENCIONES
            // --------------------------------------------------------

            bool esActualidad =
                EsConsultaActualidad(consulta);

            bool esGobernador =
                EsConsultaGobernador(consulta);

            bool esPoblacion =
                EsConsultaPoblacion(consulta);

            bool esMunicipios =
                EsConsultaMunicipios(consulta);

            bool esLocalidades =
                EsConsultaLocalidades(consulta);

            bool esDiferenciaCiudadProvincia =
                EsConsultaDiferenciaCiudadProvincia(consulta);

            bool esFaltante =
                EsConsultaFaltante(consulta);

            bool esConocimiento =
                EsConsultaConocimiento(consulta);

            bool esUbicacion =
                EsConsultaUbicacion(consulta);

            bool esDefinicion =
                EsConsultaDefinicion(consulta);

            bool esFecha =
                EsConsultaFecha(consulta);

            // --------------------------------------------------------
            // GOBERNADOR ACTUAL
            //
            // El corpus contiene gobernadores históricos.
            // Nunca debemos utilizarlos para responder una pregunta
            // sobre el gobernador actual.
            // --------------------------------------------------------

            if (esGobernador && esActualidad)
            {
                return null;
            }

            // --------------------------------------------------------
            // CONSULTAS META
            // --------------------------------------------------------

            if (esFaltante)
            {
                return await ConstruirRespuestaSobreFaltantesAsync();
            }

            if (esConocimiento)
            {
                return await ConstruirRespuestaSobreConocimientoAsync();
            }

            // --------------------------------------------------------
            // CARGAR CORPUS
            // --------------------------------------------------------

            var documentos = await _db.CorpusDocumentos
                .AsNoTracking()
                .ToListAsync();

            if (documentos.Count == 0)
                return null;

            // --------------------------------------------------------
            // CIUDAD VS PROVINCIA
            // --------------------------------------------------------

            if (esDiferenciaCiudadProvincia)
            {
                return ConstruirRespuestaCiudadVsProvincia(documentos);
            }

            // --------------------------------------------------------
            // MUNICIPIOS
            // --------------------------------------------------------

            if (esMunicipios)
            {
                return ConstruirRespuestaMunicipios(documentos);
            }

            // --------------------------------------------------------
            // LOCALIDADES
            // --------------------------------------------------------

            if (esLocalidades)
            {
                return ConstruirRespuestaLocalidades(documentos);
            }

            // --------------------------------------------------------
            // POBLACIÓN
            // --------------------------------------------------------

            if (esPoblacion)
            {
                return BuscarRespuestaPoblacion(
                    documentos,
                    consulta,
                    normalizada);
            }

            // --------------------------------------------------------
            // UBICACIÓN
            // --------------------------------------------------------

            if (esUbicacion)
            {
                return BuscarRespuestaUbicacion(
                    documentos,
                    normalizada);
            }

            // --------------------------------------------------------
            // DEFINICIÓN
            // --------------------------------------------------------

            if (esDefinicion)
            {
                return BuscarRespuestaDefinicion(
                    documentos,
                    normalizada);
            }

            // --------------------------------------------------------
            // FECHA
            // --------------------------------------------------------

            if (esFecha)
            {
                return BuscarRespuestaFecha(
                    documentos,
                    normalizada);
            }

            // --------------------------------------------------------
            // CONSULTA GENERAL
            // --------------------------------------------------------

            return BuscarRespuestaGeneral(
                documentos,
                normalizada);
        }

        // ============================================================
        // POBLACIÓN
        // ============================================================

        private string? BuscarRespuestaPoblacion(
            IEnumerable<CorpusDocumento> documentos,
            string consulta,
            string normalizada)
        {
            var lista = documentos.ToList();

            bool mencionaProvincia =
                ContienePalabra(normalizada, "provincia");

            bool mencionaCapital =
                ContienePalabra(normalizada, "capital");

            bool mencionaCorrientes =
                ContieneEntidad(normalizada, "corrientes");

            IEnumerable<CorpusDocumento> candidatos;

            if (mencionaProvincia)
            {
                candidatos = lista
                    .Where(d =>
                        NormalizarTexto(d.Titulo)
                            .Contains(
                                "provincia de corrientes",
                                StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            else if (mencionaCapital)
            {
                candidatos = lista
                    .Where(d =>
                        NormalizarTexto(d.Titulo)
                            .Contains(
                                "corrientes ciudad",
                                StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            else if (mencionaCorrientes)
            {
                candidatos = lista
                    .Where(d =>
                    {
                        var titulo = NormalizarTexto(d.Titulo);

                        return titulo.Contains(
                                   "corrientes ciudad",
                                   StringComparison.OrdinalIgnoreCase)
                               ||
                               titulo.Contains(
                                   "provincia de corrientes",
                                   StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();
            }
            else
            {
                candidatos = lista;
            }

            foreach (var documento in candidatos)
            {
                var oracion =
                    BuscarOracionPoblacion(documento.Contenido);

                if (!string.IsNullOrWhiteSpace(oracion))
                {
                    return $"{documento.Titulo}: {oracion}";
                }
            }

            // --------------------------------------------------------
            // No hay evidencia suficiente en el corpus.
            // Es preferible devolver null y permitir WebSearch antes
            // que inventar o devolver una oración irrelevante.
            // --------------------------------------------------------

            return null;
        }

        private string? BuscarOracionPoblacion(string contenido)
        {
            if (string.IsNullOrWhiteSpace(contenido))
                return null;

            var oraciones = SepararOraciones(contenido);

            string? mejor = null;
            int mejorScore = 0;

            foreach (var oracion in oraciones)
            {
                var normalizada =
                    NormalizarTexto(oracion);

                bool tieneHabitantes =
                    ContienePalabra(
                        normalizada,
                        "habitantes");

                bool tienePoblacion =
                    ContienePalabra(
                        normalizada,
                        "poblacion");

                bool tieneCenso =
                    ContienePalabra(
                        normalizada,
                        "censo");

                bool tieneIndec =
                    ContienePalabra(
                        normalizada,
                        "indec");

                if (!tieneHabitantes &&
                    !tienePoblacion &&
                    !tieneCenso &&
                    !tieneIndec)
                {
                    continue;
                }

                int score = 0;

                if (tieneHabitantes)
                    score += 50;

                if (tienePoblacion)
                    score += 30;

                if (tieneCenso)
                    score += 20;

                if (tieneIndec)
                    score += 20;

                if (ContieneFecha(normalizada))
                    score += 10;

                if (score > mejorScore)
                {
                    mejorScore = score;
                    mejor = oracion.Trim();
                }
            }

            return mejor;
        }

        // ============================================================
        // MUNICIPIOS
        // ============================================================

        private string? ConstruirRespuestaMunicipios(
            IEnumerable<CorpusDocumento> documentos)
        {
            var nombres = documentos
                .Where(d =>
                    EsDocumentoLocalidad(d.Titulo))
                .Select(d => d.Titulo.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (nombres.Count == 0)
                return null;

            return
                "En el corpus actual, CorrientesIA tiene información sobre " +
                "municipios/localidades como " +
                string.Join(", ", nombres) +
                ". Esta lista refleja los documentos disponibles " +
                "actualmente y no necesariamente el registro completo " +
                "de municipios de la provincia.";
        }

        // ============================================================
        // LOCALIDADES
        // ============================================================

        private string? ConstruirRespuestaLocalidades(
            IEnumerable<CorpusDocumento> documentos)
        {
            var localidades = documentos
                .Where(d =>
                    EsDocumentoLocalidad(d.Titulo))
                .Select(d => d.Titulo.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (localidades.Count == 0)
                return null;

            return
                "En el corpus actual, CorrientesIA tiene información sobre " +
                "localidades como " +
                string.Join(", ", localidades) +
                ". La lista corresponde a los documentos disponibles " +
                "actualmente.";
        }

        // ============================================================
        // CIUDAD VS PROVINCIA
        // ============================================================

        private string? ConstruirRespuestaCiudadVsProvincia(
            IEnumerable<CorpusDocumento> documentos)
        {
            var ciudad = documentos.FirstOrDefault(d =>
                NormalizarTexto(d.Titulo)
                    .Contains(
                        "corrientes ciudad",
                        StringComparison.OrdinalIgnoreCase));

            var provincia = documentos.FirstOrDefault(d =>
                NormalizarTexto(d.Titulo)
                    .Contains(
                        "provincia de corrientes",
                        StringComparison.OrdinalIgnoreCase));

            if (ciudad == null || provincia == null)
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

        private string? BuscarRespuestaUbicacion(
            IEnumerable<CorpusDocumento> documentos,
            string normalizada)
        {
            var entidades =
                ObtenerEntidadesConsulta(normalizada);

            if (entidades.Count == 0)
                return null;

            var puntuados =
                new List<DocumentoPuntuado>();

            foreach (var documento in documentos)
            {
                var titulo =
                    NormalizarTexto(documento.Titulo);

                var contenido =
                    NormalizarTexto(documento.Contenido);

                int score = 0;

                foreach (var entidad in entidades)
                {
                    if (ContieneEntidad(titulo, entidad))
                        score += 100;

                    if (ContieneEntidad(contenido, entidad))
                        score += 30;
                }

                if (contenido.Contains(
                    "ubicado",
                    StringComparison.OrdinalIgnoreCase))
                {
                    score += 15;
                }

                if (contenido.Contains(
                    "ubicada",
                    StringComparison.OrdinalIgnoreCase))
                {
                    score += 15;
                }

                if (contenido.Contains(
                    "provincia de corrientes",
                    StringComparison.OrdinalIgnoreCase))
                {
                    score += 10;
                }

                if (score > 0)
                {
                    puntuados.Add(
                        new DocumentoPuntuado(
                            documento,
                            score));
                }
            }

            var mejor = puntuados
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();

            if (mejor == null)
                return null;

            var contenidoOriginal =
                mejor.Documento.Contenido;

            var oracion =
                BuscarOracionUbicacion(
                    contenidoOriginal);

            if (string.IsNullOrWhiteSpace(oracion))
            {
                oracion =
                    PrimeraOracion(
                        contenidoOriginal);
            }

            if (string.IsNullOrWhiteSpace(oracion))
                return null;

            return
                $"{mejor.Documento.Titulo}: {oracion}";
        }

        private string? BuscarOracionUbicacion(
            string contenido)
        {
            var oraciones =
                SepararOraciones(contenido);

            string? mejor = null;
            int mejorScore = 0;

            foreach (var oracion in oraciones)
            {
                var normalizada =
                    NormalizarTexto(oracion);

                int score = 0;

                if (ContienePalabra(
                    normalizada,
                    "ubicado"))
                {
                    score += 50;
                }

                if (ContienePalabra(
                    normalizada,
                    "ubicada"))
                {
                    score += 50;
                }

                if (ContienePalabra(
                    normalizada,
                    "provincia"))
                {
                    score += 30;
                }

                if (ContienePalabra(
                    normalizada,
                    "corrientes"))
                {
                    score += 20;
                }

                if (ContienePalabra(
                    normalizada,
                    "departamento"))
                {
                    score += 15;
                }

                if (score > mejorScore)
                {
                    mejorScore = score;
                    mejor = oracion.Trim();
                }
            }

            return mejor;
        }

        // ============================================================
        // DEFINICIÓN
        // ============================================================

        private string? BuscarRespuestaDefinicion(
            IEnumerable<CorpusDocumento> documentos,
            string normalizada)
        {
            var entidades =
                ObtenerEntidadesConsulta(normalizada);

            if (entidades.Count == 0)
                return null;

            var puntuados =
                new List<DocumentoPuntuado>();

            foreach (var documento in documentos)
            {
                var titulo =
                    NormalizarTexto(documento.Titulo);

                var contenido =
                    NormalizarTexto(documento.Contenido);

                int score = 0;

                foreach (var entidad in entidades)
                {
                    if (ContieneEntidad(titulo, entidad))
                        score += 100;

                    if (ContieneEntidad(contenido, entidad))
                        score += 25;
                }

                if (contenido.Contains(
                    "es un",
                    StringComparison.OrdinalIgnoreCase))
                {
                    score += 15;
                }

                if (contenido.Contains(
                    "es una",
                    StringComparison.OrdinalIgnoreCase))
                {
                    score += 15;
                }

                if (score > 0)
                {
                    puntuados.Add(
                        new DocumentoPuntuado(
                            documento,
                            score));
                }
            }

            var mejor = puntuados
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();

            if (mejor == null)
                return null;

            var oracion =
                BuscarOracionDefinicion(
                    mejor.Documento.Contenido);

            if (string.IsNullOrWhiteSpace(oracion))
                oracion =
                    PrimeraOracion(
                        mejor.Documento.Contenido);

            if (string.IsNullOrWhiteSpace(oracion))
                return null;

            return
                $"{mejor.Documento.Titulo}: {oracion}";
        }

        private string? BuscarOracionDefinicion(
            string contenido)
        {
            foreach (var oracion in SepararOraciones(contenido))
            {
                var normalizada =
                    NormalizarTexto(oracion);

                if (normalizada.Contains(
                        " es un ",
                        StringComparison.OrdinalIgnoreCase) ||
                    normalizada.StartsWith(
                        "es un ",
                        StringComparison.OrdinalIgnoreCase) ||
                    normalizada.Contains(
                        " es una ",
                        StringComparison.OrdinalIgnoreCase) ||
                    normalizada.StartsWith(
                        "es una ",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return oracion.Trim();
                }
            }

            return null;
        }

        // ============================================================
        // FECHAS
        // ============================================================

        private string? BuscarRespuestaFecha(
            IEnumerable<CorpusDocumento> documentos,
            string normalizada)
        {
            var entidades =
                ObtenerEntidadesConsulta(normalizada);

            if (entidades.Count == 0)
                return null;

            var puntuados =
                new List<DocumentoPuntuado>();

            foreach (var documento in documentos)
            {
                var titulo =
                    NormalizarTexto(documento.Titulo);

                var contenido =
                    NormalizarTexto(documento.Contenido);

                int score = 0;

                foreach (var entidad in entidades)
                {
                    if (ContieneEntidad(titulo, entidad))
                        score += 100;

                    if (ContieneEntidad(contenido, entidad))
                        score += 20;
                }

                if (ContieneFecha(contenido))
                    score += 20;

                if (score > 0)
                {
                    puntuados.Add(
                        new DocumentoPuntuado(
                            documento,
                            score));
                }
            }

            var mejor = puntuados
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();

            if (mejor == null)
                return null;

            var oracion =
                BuscarOracionConFecha(
                    mejor.Documento.Contenido);

            if (string.IsNullOrWhiteSpace(oracion))
                return null;

            return
                $"{mejor.Documento.Titulo}: {oracion}";
        }

        // ============================================================
        // CONSULTA GENERAL
        // ============================================================

        private string? BuscarRespuestaGeneral(
            IEnumerable<CorpusDocumento> documentos,
            string normalizada)
        {
            var palabras =
                ObtenerPalabrasRelevantes(
                    normalizada);

            if (palabras.Count == 0)
                return null;

            var puntuados =
                new List<DocumentoPuntuado>();

            foreach (var documento in documentos)
            {
                var titulo =
                    NormalizarTexto(documento.Titulo);

                var contenido =
                    NormalizarTexto(documento.Contenido);

                int score = 0;

                foreach (var palabra in palabras)
                {
                    if (ContienePalabra(
                        titulo,
                        palabra))
                    {
                        score += 50;
                    }

                    if (ContienePalabra(
                        contenido,
                        palabra))
                    {
                        score += 5;
                    }
                }

                if (score > 0)
                {
                    puntuados.Add(
                        new DocumentoPuntuado(
                            documento,
                            score));
                }
            }

            var mejor = puntuados
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();

            if (mejor == null)
                return null;

            var oracion =
                ObtenerMejorOracion(
                    mejor.Documento.Contenido,
                    palabras);

            if (string.IsNullOrWhiteSpace(oracion))
                return null;

            return
                $"{mejor.Documento.Titulo}: {oracion}";
        }

        // ============================================================
        // RESPUESTA SOBRE LO QUE FALTA
        // ============================================================

        private async Task<string?>
            ConstruirRespuestaSobreFaltantesAsync()
        {
            var cantidadDocumentos =
                await _db.CorpusDocumentos
                    .AsNoTracking()
                    .CountAsync();

            var cantidadDatosDuros =
                await _db.DatosDuros
                    .AsNoTracking()
                    .CountAsync();

            var respuesta =
                new StringBuilder();

            respuesta.Append(
                $"Actualmente CorrientesIA dispone de " +
                $"{cantidadDocumentos} documentos en su corpus.");

            respuesta.Append(
                " La información disponible incluye la ciudad y " +
                "provincia de Corrientes, historia, Esteros del Iberá, " +
                "parques, chamamé, Río Paraná y distintas localidades.");

            if (cantidadDatosDuros == 0)
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

        // ============================================================
        // RESPUESTA SOBRE LO QUE CONOCE
        // ============================================================

        private async Task<string?>
            ConstruirRespuestaSobreConocimientoAsync()
        {
            var documentos =
                await _db.CorpusDocumentos
                    .AsNoTracking()
                    .ToListAsync();

            if (documentos.Count == 0)
                return null;

            var titulos = documentos
                .Select(d => d.Titulo)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            return
                $"Actualmente CorrientesIA tiene " +
                $"{titulos.Count} documentos en su corpus. " +
                $"Entre los temas disponibles se encuentran: " +
                $"{string.Join(", ", titulos)}.";
        }

        // ============================================================
        // DETECCIÓN DE INTENCIONES
        // ============================================================

        private bool EsConsultaPoblacion(
            string consulta)
        {
            var texto =
                NormalizarTexto(consulta);

            return
                ContienePalabra(
                    texto,
                    "poblacion") ||
                ContienePalabra(
                    texto,
                    "habitantes") ||
                ContienePalabra(
                    texto,
                    "censo");
        }

        private bool EsConsultaGobernador(
            string consulta)
        {
            var texto =
                NormalizarTexto(consulta);

            return
                ContienePalabra(
                    texto,
                    "gobernador") ||
                ContienePalabra(
                    texto,
                    "gobernadora");
        }

        private bool EsConsultaMunicipios(
            string consulta)
        {
            var texto =
                NormalizarTexto(consulta);

            return
                ContienePalabra(
                    texto,
                    "municipio") ||
                ContienePalabra(
                    texto,
                    "municipios");
        }

        private bool EsConsultaLocalidades(
            string consulta)
        {
            var texto =
                NormalizarTexto(consulta);

            return
                ContienePalabra(
                    texto,
                    "localidad") ||
                ContienePalabra(
                    texto,
                    "localidades");
        }

        private bool EsConsultaActualidad(
            string consulta)
        {
            var texto =
                NormalizarTexto(consulta);

            string[] palabrasActualidad =
            {
                "actual",
                "actualmente",
                "actualidad",
                "hoy",
                "vigente",
                "vigentes",
                "actuales",
                "quien ocupa",
                "quien es el actual",
                "quien es la actual"
            };

            return palabrasActualidad.Any(
                palabra =>
                    texto.Contains(
                        palabra,
                        StringComparison.OrdinalIgnoreCase));
        }

        private bool EsConsultaFaltante(
            string consulta)
        {
            var texto =
                NormalizarTexto(consulta);

            string[] patrones =
            {
                "que informacion no tiene",
                "que informacion le falta",
                "que datos no tiene",
                "que no tiene",
                "que no sabe",
                "que informacion falta",
                "que le falta",
                "que cosas no sabe"
            };

            return patrones.Any(
                patron =>
                    texto.Contains(
                        patron,
                        StringComparison.OrdinalIgnoreCase));
        }

        private bool EsConsultaConocimiento(
            string consulta)
        {
            var texto =
                NormalizarTexto(consulta);

            string[] patrones =
            {
                "que informacion tiene corrientesia",
                "que sabe corrientesia",
                "que conoce corrientesia",
                "que informacion conoce corrientesia",
                "que tiene corrientesia",
                "sobre que tiene informacion"
            };

            return patrones.Any(
                patron =>
                    texto.Contains(
                        patron,
                        StringComparison.OrdinalIgnoreCase));
        }

        private bool EsConsultaUbicacion(
            string consulta)
        {
            var texto =
                NormalizarTexto(consulta);

            return
                ContienePalabra(
                    texto,
                    "donde") ||
                ContienePalabra(
                    texto,
                    "ubicado") ||
                ContienePalabra(
                    texto,
                    "ubicada") ||
                ContienePalabra(
                    texto,
                    "ubicacion") ||
                ContienePalabra(
                    texto,
                    "localizado") ||
                ContienePalabra(
                    texto,
                    "localizada");
        }

        private bool EsConsultaInformacionLugar(
            string consulta)
        {
            var texto =
                NormalizarTexto(consulta);

            return texto.Contains(
                "informacion sobre",
                StringComparison.OrdinalIgnoreCase);
        }

        private bool EsConsultaDefinicion(
            string consulta)
        {
            var texto =
                NormalizarTexto(consulta);

            return
                ContienePalabra(
                    texto,
                    "significa") ||
                ContienePalabra(
                    texto,
                    "define") ||
                ContienePalabra(
                    texto,
                    "definicion") ||
                texto.Contains(
                    "que es",
                    StringComparison.OrdinalIgnoreCase);
        }

        private bool EsConsultaFecha(
            string consulta)
        {
            var texto =
                NormalizarTexto(consulta);

            return
                ContienePalabra(
                    texto,
                    "cuando") ||
                ContienePalabra(
                    texto,
                    "fecha");
        }

        private bool EsConsultaDiferenciaCiudadProvincia(
            string consulta)
        {
            var texto =
                NormalizarTexto(consulta);

            bool diferencia =
                ContienePalabra(
                    texto,
                    "diferencia");

            bool ciudad =
                ContienePalabra(
                    texto,
                    "capital") ||
                ContienePalabra(
                    texto,
                    "ciudad");

            bool provincia =
                ContienePalabra(
                    texto,
                    "provincia");

            return diferencia &&
                   ciudad &&
                   provincia;
        }

        // ============================================================
        // DOCUMENTOS DE LOCALIDADES
        // ============================================================

        private bool EsDocumentoLocalidad(
            string titulo)
        {
            var texto =
                NormalizarTexto(titulo);

            string[] conocidos =
            {
                "mercedes",
                "goya",
                "ituzaingo",
                "monte caseros",
                "saladas",
                "san roque",
                "san luis del palmar"
            };

            return conocidos.Any(
                nombre =>
                    texto.Contains(
                        nombre,
                        StringComparison.OrdinalIgnoreCase));
        }

        // ============================================================
        // ENTIDADES
        // ============================================================

        private List<string> ObtenerEntidadesConsulta(
            string normalizada)
        {
            var entidades =
                new List<string>();

            string[] conocidas =
            {
                "monte caseros",
                "san luis del palmar",
                "san roque",
                "ituzaingo",
                "mercedes",
                "goya",
                "saladas",
                "esteros del ibera",
                "parque nacional ibera",
                "parque nacional mburucuya",
                "chamame",
                "rio parana",
                "paye",
                "corrientes"
            };

            foreach (var entidad in conocidas)
            {
                if (ContieneEntidad(
                    normalizada,
                    entidad))
                {
                    entidades.Add(entidad);
                }
            }

            return entidades;
        }

        // ============================================================
        // PALABRAS RELEVANTES
        // ============================================================

        private List<string> ObtenerPalabrasRelevantes(
            string normalizada)
        {
            var genericas =
                ObtenerPalabrasGenericas();

            return normalizada
                .Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries)
                .Where(p =>
                    p.Length >= 3 &&
                    !genericas.Contains(p))
                .Distinct()
                .ToList();
        }

        private HashSet<string>
            ObtenerPalabrasGenericas()
        {
            return new HashSet<string>(
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
                StringComparer.OrdinalIgnoreCase);
        }

        // ============================================================
        // ORACIONES
        // ============================================================

        private string? ObtenerMejorOracion(
            string contenido,
            IEnumerable<string> palabras)
        {
            if (string.IsNullOrWhiteSpace(contenido))
                return null;

            var oraciones =
                SepararOraciones(contenido);

            var palabrasLista =
                palabras
                    .Select(NormalizarTexto)
                    .Where(x =>
                        !string.IsNullOrWhiteSpace(x))
                    .ToList();

            string? mejor = null;
            int mejorScore = 0;

            foreach (var oracion in oraciones)
            {
                var normalizada =
                    NormalizarTexto(oracion);

                int score = 0;

                foreach (var palabra in palabrasLista)
                {
                    if (ContienePalabra(
                        normalizada,
                        palabra))
                    {
                        score++;
                    }
                }

                if (score > mejorScore)
                {
                    mejorScore = score;
                    mejor = oracion.Trim();
                }
            }

            return mejor;
        }

        private string? BuscarOracionConFecha(
            string contenido)
        {
            if (string.IsNullOrWhiteSpace(contenido))
                return null;

            foreach (var oracion in SepararOraciones(contenido))
            {
                if (ContieneFecha(oracion))
                    return oracion.Trim();
            }

            return null;
        }

        private string PrimeraOracion(
            string? contenido)
        {
            if (string.IsNullOrWhiteSpace(contenido))
                return string.Empty;

            var oraciones =
                SepararOraciones(contenido);

            return
                oraciones.FirstOrDefault()?.Trim()
                ?? string.Empty;
        }

        private List<string> SepararOraciones(
            string contenido)
        {
            return contenido
                .Replace(
                    "\r\n",
                    "\n")
                .Split(
                    new[]
                    {
                        '.',
                        '!',
                        '?',
                        '\n'
                    },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
        }

        // ============================================================
        // FECHAS
        // ============================================================

        private bool ContieneFecha(
            string texto)
        {
            if (string.IsNullOrWhiteSpace(texto))
                return false;

            var normalizado =
                NormalizarTexto(texto);

            for (int year = 1500;
                 year <= 2100;
                 year++)
            {
                if (normalizado.Contains(
                    year.ToString(
                        CultureInfo.InvariantCulture),
                    StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // ============================================================
        // MATCHING
        // ============================================================

        private bool ContieneEntidad(
            string texto,
            string entidad)
        {
            if (string.IsNullOrWhiteSpace(texto) ||
                string.IsNullOrWhiteSpace(entidad))
            {
                return false;
            }

            var textoNormalizado =
                NormalizarTexto(texto);

            var entidadNormalizada =
                NormalizarTexto(entidad);

            if (entidadNormalizada.Contains(' '))
            {
                return textoNormalizado.Contains(
                    entidadNormalizada,
                    StringComparison.OrdinalIgnoreCase);
            }

            return ContienePalabra(
                textoNormalizado,
                entidadNormalizada);
        }

        private bool ContienePalabra(
            string texto,
            string palabra)
        {
            if (string.IsNullOrWhiteSpace(texto) ||
                string.IsNullOrWhiteSpace(palabra))
            {
                return false;
            }

            var textoNormalizado =
                NormalizarTexto(texto);

            var palabraNormalizada =
                NormalizarTexto(palabra);

            var partes =
                textoNormalizado.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries);

            return partes.Any(
                parte =>
                    parte.Equals(
                        palabraNormalizada,
                        StringComparison.OrdinalIgnoreCase));
        }

        // ============================================================
        // NORMALIZACIÓN
        // ============================================================

        private string NormalizarTexto(
            string? texto)
        {
            if (string.IsNullOrWhiteSpace(texto))
                return string.Empty;

            var normalizado =
                texto.Normalize(
                    NormalizationForm.FormD);

            var sb =
                new StringBuilder();

            foreach (var caracter in normalizado)
            {
                var categoria =
                    CharUnicodeInfo.GetUnicodeCategory(
                        caracter);

                if (categoria ==
                    UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(caracter) ||
                    char.IsWhiteSpace(caracter))
                {
                    sb.Append(
                        char.ToLowerInvariant(
                            caracter));
                }
                else
                {
                    sb.Append(' ');
                }
            }

            return string.Join(
                " ",
                sb.ToString()
                    .Split(
                        ' ',
                        StringSplitOptions.RemoveEmptyEntries));
        }

        // ============================================================
        // TIPO AUXILIAR PARA RANKING
        // ============================================================

        private sealed class DocumentoPuntuado
        {
            public CorpusDocumento Documento { get; }
            public int Score { get; }

            public DocumentoPuntuado(
                CorpusDocumento documento,
                int score)
            {
                Documento = documento;
                Score = score;
            }
        }
    }
}