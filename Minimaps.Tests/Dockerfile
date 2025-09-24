FROM mcr.microsoft.com/dotnet/sdk:10.0 AS base
WORKDIR /src
COPY ./ ./
RUN dotnet restore
RUN dotnet build Minimaps.Tests/Minimaps.Tests.csproj --no-restore

FROM base AS test
WORKDIR /src/Minimaps.Tests
CMD ["dotnet", "test", "--no-build", "--logger", "console;verbosity=normal"]