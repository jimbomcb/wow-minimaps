FROM mcr.microsoft.com/dotnet/sdk:10.0 AS base
WORKDIR /src

# Copy solution file and all project files for restore
COPY ./ ./

RUN dotnet restore

# Build everything - corrected path
RUN dotnet build Minimaps.Tests/Minimaps.Tests.csproj --no-restore

# Test stage
FROM base AS test
WORKDIR /src/Minimaps.Tests
CMD ["dotnet", "test", "--no-build", "--filter", "BlizztrackTests.TestBlizztrack_MapDB2", "--logger", "console;verbosity=normal"]