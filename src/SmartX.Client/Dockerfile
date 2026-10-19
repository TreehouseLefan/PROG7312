# Build context: repository root (see docker-compose.yml)

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props ./
COPY src/SmartX.Core/SmartX.Core.csproj src/SmartX.Core/
COPY src/SmartX.Client/SmartX.Client.csproj src/SmartX.Client/
RUN dotnet restore src/SmartX.Client/SmartX.Client.csproj

COPY src/SmartX.Core/ src/SmartX.Core/
COPY src/SmartX.Client/ src/SmartX.Client/
RUN dotnet publish src/SmartX.Client/SmartX.Client.csproj -c Release -o /app/publish --no-restore

# ---------- static web server ----------
FROM nginx:1.27-alpine AS final
COPY src/SmartX.Client/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
EXPOSE 80
