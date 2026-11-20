# Image digiwells/dwis.blackboard.semantic (specification 10.1, R-ENG-2).
# Build: docker build -t digiwells/dwis.blackboard.semantic:dev .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet publish src/DWIS.Semantic.Host/DWIS.Semantic.Host.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# The HTTP API on 8080 and the gRPC service on 8081 (HTTP/2 without TLS needs a port of its own: without TLS
# there is no protocol negotiation, so Kestrel serves HTTP/2 only on an endpoint that is HTTP/2 only). Both
# come from the Kestrel section of appsettings.json.
EXPOSE 8080 8081
USER app
ENTRYPOINT ["dotnet", "DWIS.Semantic.Host.dll"]
