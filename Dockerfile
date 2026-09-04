FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /source

COPY ["global.json", "."]
COPY ["Directory.Build.props", "."]
COPY ["Directory.Packages.props", "."]
COPY ["Concourse.slnx", "."]
COPY ["src/Concourse.Api/Concourse.Api.csproj", "src/Concourse.Api/"]
COPY ["src/Concourse.Application/Concourse.Application.csproj", "src/Concourse.Application/"]
COPY ["src/Concourse.Domain/Concourse.Domain.csproj", "src/Concourse.Domain/"]
COPY ["src/Concourse.Infrastructure/Concourse.Infrastructure.csproj", "src/Concourse.Infrastructure/"]

RUN dotnet restore Concourse.slnx

COPY . .

RUN dotnet publish src/Concourse.Api/Concourse.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM runtime AS final

WORKDIR /app
USER $APP_UID

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Concourse.Api.dll"]
