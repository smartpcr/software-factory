# ---- Stage 1: UI build ----
FROM node:20-alpine AS ui-build
WORKDIR /build/ui
COPY src/WorkflowUI/package.json src/WorkflowUI/package-lock.json ./
RUN npm ci --ignore-scripts
COPY src/WorkflowUI/ ./
RUN npm run build

# ---- Stage 2: Server build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS server-build
WORKDIR /build
COPY Directory.Build.props Directory.Packages.props global.json nuget.config version.json ./
COPY Workflow.sln ./
COPY src/ src/
RUN dotnet publish src/WorkflowUi.Server/WorkflowUi.Server.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained false \
    -o /app \
    /p:InvariantGlobalization=true

# ---- Stage 3: Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime

# Labels
LABEL org.opencontainers.image.source="https://github.com/smartpcr/workflow"
LABEL org.opencontainers.image.title="workflow-ui"
LABEL org.opencontainers.image.description="Workflow UI Server"

# Non-root user
RUN addgroup -g 1000 appgroup && adduser -u 1000 -G appgroup -D appuser

WORKDIR /app

# Copy server publish output
COPY --from=server-build /app ./

# Copy UI build output into wwwroot
COPY --from=ui-build /build/ui/dist ./wwwroot/

# Environment defaults
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV WorkflowUi__Persistence__WorkflowsRoot=/data/workflows
ENV WorkflowUi__Persistence__RunsRoot=/data/runs

# Create data directories
RUN mkdir -p /data/workflows /data/runs && \
    chown -R appuser:appgroup /data /app

EXPOSE 8080

USER 1000:1000

HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:8080/api/health || exit 1

ENTRYPOINT ["dotnet", "WorkflowUi.Server.dll"]
