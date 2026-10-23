# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json SmartX.sln ./
COPY src/SmartX.Core/SmartX.Core.csproj src/SmartX.Core/
COPY src/SmartX.Api/SmartX.Api.csproj src/SmartX.Api/
RUN dotnet restore src/SmartX.Api/SmartX.Api.csproj
COPY src/ src/
RUN dotnet publish src/SmartX.Api/SmartX.Api.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SmartX.Api.dll"]
