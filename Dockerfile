# Image digiwells/dwis.blackboard.semantic (specification 10.1, R-ENG-2).
# Build: docker build -t digiwells/dwis.blackboard.semantic:dev .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet publish src/DWIS.Semantic.Host/DWIS.Semantic.Host.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "DWIS.Semantic.Host.dll"]
