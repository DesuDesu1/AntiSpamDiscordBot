FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/AntiSpam.Contracts/AntiSpam.Contracts.csproj AntiSpam.Contracts/
COPY src/AntiSpam.Bot/AntiSpam.Bot.csproj AntiSpam.Bot/
RUN dotnet restore AntiSpam.Bot/AntiSpam.Bot.csproj

COPY src/AntiSpam.Contracts/ AntiSpam.Contracts/
COPY src/AntiSpam.Bot/ AntiSpam.Bot/
RUN dotnet publish AntiSpam.Bot/AntiSpam.Bot.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "AntiSpam.Bot.dll"]
