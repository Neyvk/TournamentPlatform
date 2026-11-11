FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/PlayersService/PlayersService.csproj src/PlayersService/
RUN dotnet restore src/PlayersService/PlayersService.csproj
COPY src/PlayersService/ src/PlayersService/
RUN dotnet publish src/PlayersService/PlayersService.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "PlayersService.dll"]
