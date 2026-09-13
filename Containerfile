FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY TerraScale.Reviewer.csproj .
RUN dotnet restore TerraScale.Reviewer.csproj
COPY . .
RUN dotnet publish TerraScale.Reviewer.csproj --configuration Release --no-restore --output /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "TerraScale.Reviewer.dll"]
