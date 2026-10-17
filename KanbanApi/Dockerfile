# Build from the repository root: the API references KanbanContracts.
FROM mcr.microsoft.com/dotnet/sdk:10.0.103@sha256:e362a8dbcd691522456da26a5198b8f3ca1d7641c95624fadc5e3e82678bd08a AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json ./
COPY KanbanContracts/ KanbanContracts/
COPY KanbanApi/ KanbanApi/
# Keep restore, publish and removal in one layer: a small Docker disk must not
# retain the large all-platform Magick.NET NuGet cache in an intermediate layer.
# Discard other platforms before publish duplicates the selected native library.
# This keeps peak disk usage within the local 7.8 GiB Docker allocation.
RUN dotnet restore KanbanApi/KanbanApi.csproj --no-http-cache -r linux-$TARGETARCH \
    && find /root/.nuget/packages/magick.net-q8-anycpu -mindepth 3 -maxdepth 3 -type d -path '*/runtimes/*' ! -name "linux-$TARGETARCH" -exec rm -rf {} + \
    && dotnet publish KanbanApi/KanbanApi.csproj --no-restore -c Release -r linux-$TARGETARCH --self-contained false -o /app/publish /p:UseAppHost=false \
    && dotnet nuget locals all --clear \
    && rm -rf /root/.nuget/packages /src/KanbanApi/bin /src/KanbanApi/obj /src/KanbanContracts/bin /src/KanbanContracts/obj

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS final
WORKDIR /app
COPY --from=build /app/publish/ ./
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=15s --start-period=30s --retries=3 CMD ["dotnet", "KanbanApi.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "KanbanApi.dll"]
