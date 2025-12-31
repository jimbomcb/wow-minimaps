FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release

# node.js 22 for TS
RUN apt-get update && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && \
    apt-get install -y nodejs && rm -rf /var/lib/apt/lists/*

WORKDIR /src
COPY ["External/DBCD/Directory.Build.props", "External/DBCD/"]
COPY ["External/DBCD/Directory.Build.targets", "External/DBCD/"]
COPY ["Minimaps.Frontend/Minimaps.Frontend.csproj", "Minimaps.Frontend/"]
COPY ["Minimaps.Aspire.ServiceDefaults/Minimaps.Aspire.ServiceDefaults.csproj", "Minimaps.Aspire.ServiceDefaults/"]
COPY ["Minimaps.Database/Minimaps.Database.csproj", "Minimaps.Database/"]
COPY ["Minimaps.Shared/Minimaps.Shared.csproj", "Minimaps.Shared/"]
COPY ["External/DBCD/DBCD/DBCD.csproj", "External/DBCD/DBCD/"]
COPY ["External/DBCD/DBCD.IO/DBCD.IO.csproj", "External/DBCD/DBCD.IO/"]
RUN dotnet restore "./Minimaps.Frontend/Minimaps.Frontend.csproj"
COPY . .
WORKDIR "/src/Minimaps.Frontend"
RUN dotnet build "./Minimaps.Frontend.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Minimaps.Frontend.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Minimaps.Frontend.dll"]