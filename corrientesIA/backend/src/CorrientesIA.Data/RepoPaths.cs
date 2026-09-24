namespace CorrientesIA.Data;

/// <summary>
/// Ubica carpetas compartidas del repo (como "model/") sin depender de rutas
/// relativas fragiles que cambian segun si corres con "dotnet run", desde el
/// binario publicado, o dentro de un contenedor Docker.
/// </summary>
public static class RepoPaths
{
    /// <summary>
    /// Sube desde <paramref name="desde"/> (por defecto, la carpeta del binario
    /// en ejecucion) hasta encontrar la raiz del repo (la carpeta que contiene
    /// "docker-compose.yml") y devuelve su subcarpeta "model/", donde viven
    /// gpt-mini.pt y tokenizer.json compartidos entre Training y la Api.
    /// </summary>
    public static string CarpetaModelo(string? desde = null)
    {
        var dir = new DirectoryInfo(desde ?? AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docker-compose.yml")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException(
                "No se encontro la raiz del repo (docker-compose.yml) subiendo desde " +
                (desde ?? AppContext.BaseDirectory) +
                ". Si estas corriendo desde un contenedor u otro entorno, configura " +
                "explicitamente ModelSettings:CheckpointDir / TrainingSettings:CarpetaCheckpoints.");

        return Path.Combine(dir.FullName, "model");
    }
}
