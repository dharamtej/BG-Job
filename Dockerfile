# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files first for layer-cached restore
COPY BackgroundJobs/CareerPandaWeb/CareerPandaWeb.csproj     CareerPandaWeb/
COPY BackgroundJobs/CareerPandaBL/CareerPandaBL.csproj       CareerPandaBL/
COPY BackgroundJobs/DataAccess/DataAccess.csproj             DataAccess/
COPY BackgroundJobs/Framework/Framework.csproj               Framework/

RUN dotnet restore CareerPandaWeb/CareerPandaWeb.csproj

# Copy full source and publish
COPY BackgroundJobs/ .
WORKDIR /src/CareerPandaWeb
RUN dotnet publish CareerPandaWeb.csproj -c Release -o /app/publish /p:UseAppHost=false

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

# Shell-form CMD so $PORT is expanded at runtime from Railway's injected env var
CMD ASPNETCORE_URLS=http://+:${PORT:-8080} dotnet CareerPanda.Web.dll
