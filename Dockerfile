# syntax=docker/dockerfile:1

# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first (cached unless the csproj changes).
COPY src/WebMail/WebMail.csproj src/WebMail/
RUN dotnet restore src/WebMail/WebMail.csproj

# Build + publish.
COPY src/ src/
RUN dotnet publish src/WebMail/WebMail.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# SQLite db + DataProtection keys live here; mount a volume to persist them.
RUN mkdir -p /app/data
VOLUME /app/data

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

ENTRYPOINT ["dotnet", "WebMail.dll"]
