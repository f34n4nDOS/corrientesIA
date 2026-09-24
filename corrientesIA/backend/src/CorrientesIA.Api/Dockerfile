FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/CorrientesIA.Api/CorrientesIA.Api.csproj", "src/CorrientesIA.Api/"]
COPY ["src/CorrientesIA.Data/CorrientesIA.Data.csproj", "src/CorrientesIA.Data/"]
RUN dotnet restore "src/CorrientesIA.Api/CorrientesIA.Api.csproj"
COPY . .
RUN dotnet publish "src/CorrientesIA.Api/CorrientesIA.Api.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CorrientesIA.Api.dll"]
