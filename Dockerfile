FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 5000

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["DownloadFY/DownloadFY.csproj", "DownloadFY/"]
RUN dotnet restore "DownloadFY/DownloadFY.csproj"
COPY . .
WORKDIR "/src/DownloadFY"
RUN dotnet build "DownloadFY.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "DownloadFY.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
RUN apt-get update && \
    apt-get install -y ffmpeg && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

COPY --from=publish /app/publish .

ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "DownloadFY.dll"]
