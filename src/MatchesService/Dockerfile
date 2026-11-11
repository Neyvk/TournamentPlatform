FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/MatchesService/MatchesService.csproj src/MatchesService/
RUN dotnet restore src/MatchesService/MatchesService.csproj
COPY src/MatchesService/ src/MatchesService/
RUN dotnet publish src/MatchesService/MatchesService.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MatchesService.dll"]
