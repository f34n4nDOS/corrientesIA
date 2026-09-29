$ErrorActionPreference = "Stop"

$ApiUrl = "https://corrientesia-production.up.railway.app/api/Chat"

$Preguntas = @(
    "¿Cuál es la capital de la provincia de Corrientes?",
    "¿Qué localidades importantes tiene la provincia de Corrientes?",
    "¿Dónde se encuentra Monte Caseros?",
    "¿Qué información tiene CorrientesIA sobre Monte Caseros?",
    "¿Qué información tiene CorrientesIA sobre los Esteros del Iberá?",
    "¿Cuál es la diferencia entre Corrientes capital y la provincia de Corrientes?",
    "¿Qué municipios de Corrientes conoce CorrientesIA?",
    "¿Cuál es la población de Corrientes?",
    "¿Quién es el gobernador actual de Corrientes?",
    "¿Qué información no tiene CorrientesIA sobre Corrientes?"
)

$Resultados = @()

Write-Host ""
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "       CORRIENTESIA - BASELINE V1" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""

$i = 0

foreach ($Pregunta in $Preguntas) {

    $i++

    Write-Host "[$i/$($Preguntas.Count)] $Pregunta" -ForegroundColor Yellow

    $Body = @{
        Mensaje = $Pregunta
    } | ConvertTo-Json -Depth 5

    $Cronometro = [System.Diagnostics.Stopwatch]::StartNew()

    try {

        $Respuesta = Invoke-RestMethod `
            -Uri $ApiUrl `
            -Method Post `
            -ContentType "application/json" `
            -Body $Body

        $Cronometro.Stop()

        $TextoRespuesta = [string]$Respuesta.Respuesta

        if ([string]::IsNullOrWhiteSpace($TextoRespuesta)) {
            $TextoRespuesta = [string]$Respuesta
        }

        $Resultados += [PSCustomObject]@{
            Numero = $i
            Pregunta = $Pregunta
            TiempoMs = $Cronometro.ElapsedMilliseconds
            Respuesta = $TextoRespuesta
            Error = ""
        }

        Write-Host "   Tiempo: $($Cronometro.ElapsedMilliseconds) ms" -ForegroundColor Green
        Write-Host "   Respuesta: $TextoRespuesta"
    }
    catch {

        $Cronometro.Stop()

        $Resultados += [PSCustomObject]@{
            Numero = $i
            Pregunta = $Pregunta
            TiempoMs = $Cronometro.ElapsedMilliseconds
            Respuesta = ""
            Error = $_.Exception.Message
        }

        Write-Host "   ERROR: $($_.Exception.Message)" -ForegroundColor Red
    }

    Write-Host ""
}

$Archivo = Join-Path $PSScriptRoot "baseline-v1.csv"

$Resultados | Export-Csv `
    -Path $Archivo `
    -NoTypeInformation `
    -Encoding UTF8

$Exitosas = @($Resultados | Where-Object { $_.Error -eq "" })
$Errores = @($Resultados | Where-Object { $_.Error -ne "" })

if ($Exitosas.Count -gt 0) {
    $Promedio = ($Exitosas | Measure-Object -Property TiempoMs -Average).Average
    $Minimo = ($Exitosas | Measure-Object -Property TiempoMs -Minimum).Minimum
    $Maximo = ($Exitosas | Measure-Object -Property TiempoMs -Maximum).Maximum
}
else {
    $Promedio = 0
    $Minimo = 0
    $Maximo = 0
}

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "                 RESULTADO" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

Write-Host ""
Write-Host "Consultas:        $($Resultados.Count)"
Write-Host "Exitosas:         $($Exitosas.Count)" -ForegroundColor Green
Write-Host "Errores:          $($Errores.Count)" -ForegroundColor Red
Write-Host "Tiempo promedio:  $([math]::Round($Promedio, 0)) ms"
Write-Host "Tiempo mínimo:    $Minimo ms"
Write-Host "Tiempo máximo:    $Maximo ms"
Write-Host ""
Write-Host "Resultados guardados en:"
Write-Host $Archivo -ForegroundColor Cyan
Write-Host ""