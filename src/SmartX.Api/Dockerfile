# Build context: repository root 

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props ./
COPY src/SmartX.Core/SmartX.Core.csproj src/SmartX.Core/
COPY src/SmartX.Api/SmartX.Api.csproj src/SmartX.Api/
RUN dotnet restore src/SmartX.Api/SmartX.Api.csproj

COPY src/SmartX.Core/ src/SmartX.Core/
COPY src/SmartX.Api/ src/SmartX.Api/
RUN dotnet publish src/SmartX.Api/SmartX.Api.csproj -c Release -o /app/publish --no-restore

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# The image runs as the lesser user "app" user, so gives it the upload folder.
	RUN mkdir -p /app/App_Data/uploads && chown -R $APP_UID /app/App_Data
USER $APP_UID

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SmartX.Api.dll"]
