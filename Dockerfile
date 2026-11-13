FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /workspace
COPY src/Smartgn.Telemetry/Smartgn.Telemetry.csproj src/Smartgn.Telemetry/
RUN dotnet restore src/Smartgn.Telemetry/Smartgn.Telemetry.csproj
COPY src/Smartgn.Telemetry/ src/Smartgn.Telemetry/
RUN dotnet publish src/Smartgn.Telemetry/Smartgn.Telemetry.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN mkdir /data && chown 10001:10001 /data
COPY tools/health.sh /app/health.sh
COPY --from=build /app .
USER 10001:10001
EXPOSE 8085 9084
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s CMD ["bash", "/app/health.sh"]
ENTRYPOINT ["dotnet", "Smartgn.Telemetry.dll"]
