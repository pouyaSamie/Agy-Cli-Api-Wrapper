FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/AgyApi/AgyApi.csproj", "src/AgyApi/"]
RUN dotnet restore "src/AgyApi/AgyApi.csproj"
COPY . .
WORKDIR "/src/src/AgyApi"
RUN dotnet publish "AgyApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5200
ENV ASPNETCORE_URLS=http://0.0.0.0:5200
ENV Agy__DatabasePath=/app/data/agy_gateway.db
COPY --from=build /app/publish .

VOLUME ["/root/.gemini", "/workspace", "/app/data"]
ENTRYPOINT ["dotnet", "AgyApi.dll"]
