# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json SmartX.sln ./
COPY src/SmartX.Core/SmartX.Core.csproj src/SmartX.Core/
COPY src/SmartX.DeviceSimulator/SmartX.DeviceSimulator.csproj src/SmartX.DeviceSimulator/
RUN dotnet restore src/SmartX.DeviceSimulator/SmartX.DeviceSimulator.csproj
COPY src/ src/
RUN dotnet publish src/SmartX.DeviceSimulator/SmartX.DeviceSimulator.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "SmartX.DeviceSimulator.dll"]
