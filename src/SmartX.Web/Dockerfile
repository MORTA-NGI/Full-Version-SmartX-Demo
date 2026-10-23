# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json SmartX.sln ./
COPY src/SmartX.Core/SmartX.Core.csproj src/SmartX.Core/
COPY src/SmartX.Web/SmartX.Web.csproj src/SmartX.Web/
RUN dotnet restore src/SmartX.Web/SmartX.Web.csproj
COPY src/ src/
RUN dotnet publish src/SmartX.Web/SmartX.Web.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SmartX.Web.dll"]
