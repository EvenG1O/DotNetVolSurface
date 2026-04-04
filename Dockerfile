
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src


COPY ["IvSurfaceBuilder/IvSurfaceBuilder.csproj", "IvSurfaceBuilder/"]
RUN dotnet restore "IvSurfaceBuilder/IvSurfaceBuilder.csproj"


COPY . .
WORKDIR "/src/IvSurfaceBuilder"
RUN dotnet publish "IvSurfaceBuilder.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .


ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000

ENTRYPOINT ["dotnet", "IvSurfaceBuilder.dll"]
