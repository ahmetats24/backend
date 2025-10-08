# --- 1. Build aşaması ---
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

# Proje dosyasını kopyala ve restore
COPY backend.csproj ./
RUN dotnet restore

# Tüm dosyaları kopyala ve build et
COPY . ./
RUN dotnet publish -c Release -o out

# --- 2. Runtime aşaması ---
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

# Build sonuçlarını runtime container’a kopyala
COPY --from=build /app/out .

# API portu
EXPOSE 10000

# API başlat
ENTRYPOINT ["dotnet", "backend.dll"]
