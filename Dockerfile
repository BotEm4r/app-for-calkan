FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Proje dosyalarını kopyala ve restore et
COPY *.csproj ./
RUN dotnet restore

# Diğer her şeyi kopyala ve derle
COPY . ./
RUN dotnet publish -c Release -o out

# Çalıştırma aşaması
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/out .

# Railway'in atadığı port üzerinden dinleyecek
ENV PORT=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "CalkanGsmWeb.dll"]
