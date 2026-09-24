# CorrientesIA

Mini LLM propio (transformer entrenado desde cero con TorchSharp, sin APIs externas)
que responde y analiza datos sobre la provincia de Corrientes, Argentina.

## Stack
- **Entrenamiento / modelo**: C# + [TorchSharp](https://github.com/dotnet/TorchSharp) (bindings oficiales de libtorch)
- **Backend**: ASP.NET Core Web API
- **Base de datos**: MySQL (datos duros + corpus de entrenamiento)
- **Frontend**: Angular (standalone components)
- **Deploy previsto**: Frontend → Hostinger (estático) · Backend + MySQL → Railway (Docker)

## Estructura
```
corrientesIA/
├── backend/
│   ├── CorrientesIA.sln
│   └── src/
│       ├── CorrientesIA.Api/         # API REST (ASP.NET Core)
│       ├── CorrientesIA.Training/    # GPT-mini + tokenizer BPE (TorchSharp)
│       ├── CorrientesIA.Data/        # EF Core + MySQL (entidades compartidas)
│       └── CorrientesIA.Scraper/     # Recolección de corpus (Wikipedia, gob, INDEC, turismo)
├── frontend/                         # Angular
├── db/schema.sql                     # Esquema MySQL + datos de ejemplo
└── docker-compose.yml                # Levanta MySQL + API local
```

## Arquitectura del modelo
El "cerebro" es un **GPT-mini decoder-only** (4 capas, embedding 192, 4 heads,
contexto de 128 tokens) entrenado desde cero sobre un corpus propio en español
acotado al dominio (historia, turismo, geografía, trámites de Corrientes).

Como un transformer chico entrenado con pocos datos **no es confiable para
hechos concretos** (fechas, cifras, direcciones), esos datos viven aparte en
MySQL (`Lugares`, `DatosDuros`) y la API los combina con la generación del
modelo (arquitectura de "grounding" casera, no RAG con embeddings externos).

## Cómo levantar el entorno local

### 1. Base de datos
```bash
docker compose up -d mysql
```
Esto crea la base `corrientesia` y corre `db/schema.sql` automáticamente
(incluye un par de filas de ejemplo para probar la API sin esperar al scraper).

### 2. Backend (API)
```bash
cd backend
dotnet restore
dotnet run --project src/CorrientesIA.Api
```
Swagger disponible en `http://localhost:5080/swagger` (o el puerto que asigne `dotnet run`).

### 3. Scraper (arma el corpus)
```bash
dotnet run --project src/CorrientesIA.Scraper
```

### 4. Entrenamiento
```bash
dotnet run --project src/CorrientesIA.Training
```

### 5. Frontend
```bash
cd frontend
npm install
npm start
```
App en `http://localhost:4200`, consumiendo la API en `http://localhost:5080/api`.

## Estado actual (esqueleto inicial)
- [x] Estructura de soluciones/proyectos .NET
- [x] Arquitectura GPT-mini definida en TorchSharp (`Training/Model/GptMini.cs`)
- [x] Esquema MySQL con tablas de grounding y corpus
- [x] API con endpoint `/api/chat` (responde eco hasta que haya modelo entrenado)
- [x] Frontend Angular con chat básico conectado a la API
- [ ] Tokenizador BPE (lógica real de entrenamiento de vocabulario)
- [ ] Scraper implementado por fuente (Wikipedia, gobierno, INDEC, turismo)
- [ ] Loop de entrenamiento completo + checkpoints
- [ ] Carga del modelo entrenado en `InferenceService`
- [ ] Deploy: Dockerfile de API listo para Railway, front para build estático en Hostinger

## Próximos pasos sugeridos
1. Implementar el scraper por fuente y poblar `CorpusDocumentos`.
2. Implementar `BpeTokenizer.Train()` sobre ese corpus.
3. Completar el loop de entrenamiento en `CorrientesIA.Training`.
4. Conectar `InferenceService` al checkpoint entrenado.
