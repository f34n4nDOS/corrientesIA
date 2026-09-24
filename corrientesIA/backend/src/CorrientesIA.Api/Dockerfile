# El contexto de build es la raiz del repo (corrientesIA/), no backend/,
# porque necesitamos copiar tambien la carpeta model/ (gpt-mini.pt +
# tokenizer.json) que vive al lado de backend/, no adentro.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiamos primero solo los .csproj para aprovechar la cache de "dotnet restore"
# entre builds. Los tres son necesarios: Api referencia a Data y a Training.
COPY ["backend/src/CorrientesIA.Api/CorrientesIA.Api.csproj", "backend/src/CorrientesIA.Api/"]
COPY ["backend/src/CorrientesIA.Data/CorrientesIA.Data.csproj", "backend/src/CorrientesIA.Data/"]
COPY ["backend/src/CorrientesIA.Training/CorrientesIA.Training.csproj", "backend/src/CorrientesIA.Training/"]
RUN dotnet restore "backend/src/CorrientesIA.Api/CorrientesIA.Api.csproj"

COPY backend/ backend/
RUN dotnet publish "backend/src/CorrientesIA.Api/CorrientesIA.Api.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# Modelo ya entrenado (gpt-mini.pt + tokenizer.json), para que la Api arranque
# sirviendo respuestas reales en vez de caer en modo eco.
COPY model/ ./model/

ENV ASPNETCORE_URLS=http://+:8080
ENV ModelSettings__CheckpointDir=/app/model
EXPOSE 8080
ENTRYPOINT ["dotnet", "CorrientesIA.Api.dll"]
