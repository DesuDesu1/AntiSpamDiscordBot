FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/AntiSpam.Contracts/AntiSpam.Contracts.csproj AntiSpam.Contracts/
COPY src/AntiSpam.Gateway/AntiSpam.Gateway.csproj AntiSpam.Gateway/
RUN dotnet restore AntiSpam.Gateway/AntiSpam.Gateway.csproj

COPY src/AntiSpam.Contracts/ AntiSpam.Contracts/
COPY src/AntiSpam.Gateway/ AntiSpam.Gateway/
RUN dotnet publish AntiSpam.Gateway/AntiSpam.Gateway.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "AntiSpam.Gateway.dll"]
