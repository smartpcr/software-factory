# Phase 7.7 — multi-stage Dockerfile for the Forge.Api service.
# Build: dotnet publish in a SDK container, copy the output into the runtime
# image. SQLite DB lives on a mounted volume at /data/forge.db (set
# Forge:Db:Path=/data/forge.db in the env).

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the project + restore first so subsequent rebuilds reuse the layer.
COPY src/forge/Forge.Api/Forge.Api.csproj ./Forge.Api/
RUN dotnet restore ./Forge.Api/Forge.Api.csproj

COPY src/forge/Forge.Api/ ./Forge.Api/
RUN dotnet publish ./Forge.Api/Forge.Api.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:5180
ENV Forge__Db__Path=/data/forge.db
EXPOSE 5180
VOLUME ["/data"]
COPY --from=build /app ./

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
  CMD wget --quiet --spider http://localhost:5180/api/health || exit 1

ENTRYPOINT ["dotnet", "Forge.Api.dll"]
