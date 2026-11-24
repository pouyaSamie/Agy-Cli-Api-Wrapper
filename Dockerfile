FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/AgyApi/AgyApi.csproj", "src/AgyApi/"]
RUN dotnet restore "src/AgyApi/AgyApi.csproj"
COPY src/AgyApi/ src/AgyApi/
WORKDIR "/src/src/AgyApi"
RUN dotnet publish "AgyApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime image: mount the host `agy` binary to /usr/local/bin/agy (see docker-compose.yml)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN mkdir -p /app/data /workspace /home/app/.gemini \
    && chown -R app:app /app /workspace /home/app

EXPOSE 5200
ENV ASPNETCORE_URLS=http://0.0.0.0:5200
ENV Agy__BindAddress=0.0.0.0
ENV Agy__ExecutablePath=/usr/local/bin/agy
ENV Agy__DatabasePath=/app/data/agy_gateway.db
ENV Agy__WorkspaceDirectory=/workspace
ENV HOME=/home/app

COPY --from=build --chown=app:app /app/publish .

USER app

VOLUME ["/home/app/.gemini", "/workspace", "/app/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/5200 && printf 'GET /healthz HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q '200'"]

ENTRYPOINT ["dotnet", "AgyApi.dll"]
