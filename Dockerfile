# Build asamasinda .NET 8 SDK kullaniyoruz
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
WORKDIR /app

# Proje dosyalarini kopyala ve restore et
COPY *.csproj ./
RUN dotnet restore

# Tum dosyalari kopyala ve derle
COPY . ./
RUN dotnet publish -c Release -o out

# Calisma asamasinda sadece ASP.NET Runtime yeterli (Linux tabanli)
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build-env /app/out .

# Veritabani icin data klasoru
RUN mkdir -p data

# Railway'in verdigi PORT ortam degiskenini kullanacak
ENV PORT=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "CalkanGsmWeb.dll"]
