FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/TournamentsService/TournamentsService.csproj src/TournamentsService/
RUN dotnet restore src/TournamentsService/TournamentsService.csproj
COPY src/TournamentsService/ src/TournamentsService/
RUN dotnet publish src/TournamentsService/TournamentsService.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TournamentsService.dll"]
