FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/RatingService/RatingService.csproj src/RatingService/
RUN dotnet restore src/RatingService/RatingService.csproj
COPY src/RatingService/ src/RatingService/
RUN dotnet publish src/RatingService/RatingService.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "RatingService.dll"]
