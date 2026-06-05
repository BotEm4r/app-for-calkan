# Windows tabanlı bir .NET 8.0 SDK imajı kullanıyoruz
FROM mcr.microsoft.com/dotnet/sdk:8.0-windowsservercore-ltsc2022 AS build
WORKDIR /src

# Proje dosyalarını kopyala
COPY ["CalkanGsmWeb.csproj", "./"]
RUN dotnet restore "CalkanGsmWeb.csproj"

# Tüm kaynak kodları kopyala ve yayınla (publish)
COPY . .
RUN dotnet publish "CalkanGsmWeb.csproj" -c Release -o /app/publish

# Çalışma zamanı imajı
FROM mcr.microsoft.com/dotnet/runtime:8.0-windowsservercore-ltsc2022 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Uygulamayı başlat
ENTRYPOINT ["CalkanGsmWeb.exe"]