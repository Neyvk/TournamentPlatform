FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/IdentityService/IdentityService.csproj src/IdentityService/
RUN dotnet restore src/IdentityService/IdentityService.csproj
COPY src/IdentityService/ src/IdentityService/
RUN dotnet publish src/IdentityService/IdentityService.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "IdentityService.dll"]
