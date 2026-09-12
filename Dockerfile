FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/DevSup.Api/DevSup.Api.csproj", "src/DevSup.Api/"]
COPY ["src/DevSup.Core/DevSup.Core.csproj", "src/DevSup.Core/"]
COPY ["src/DevSup.Infrastructure/DevSup.Infrastructure.csproj", "src/DevSup.Infrastructure/"]
RUN dotnet restore "src/DevSup.Api/DevSup.Api.csproj"

COPY . .
RUN dotnet publish "src/DevSup.Api/DevSup.Api.csproj" --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DevSup.Api.dll"]