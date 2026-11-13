# syntax=docker/dockerfile:1

# ---- build -------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore in its own layer: it only re-runs when package versions or project files change.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/OrderSaga.Core/OrderSaga.Core.csproj src/OrderSaga.Core/
COPY src/OrderSaga.Downstream/OrderSaga.Downstream.csproj src/OrderSaga.Downstream/
COPY src/OrderSaga.Workflows/OrderSaga.Workflows.csproj src/OrderSaga.Workflows/
COPY src/OrderSaga.App/OrderSaga.App.csproj src/OrderSaga.App/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore src/OrderSaga.App/OrderSaga.App.csproj

COPY .editorconfig ./
COPY src/ src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/OrderSaga.App/OrderSaga.App.csproj \
        --configuration Release --no-restore --output /app/publish -p:UseAppHost=false

# ---- runtime -----------------------------------------------------------------------------------------
# Chiseled Ubuntu: no shell, no package manager, and a non-root "app" user (UID 1654) baked in.
# The Temporal SDK's native core (a Rust library) links against glibc, which the noble chiseled image provides.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
LABEL org.opencontainers.image.title="order-saga-temporal" \
      org.opencontainers.image.source="https://github.com/hekimcan206/order-saga-temporal" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --link /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "OrderSaga.App.dll"]
