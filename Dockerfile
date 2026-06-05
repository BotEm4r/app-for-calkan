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

# --- DLL İSMİ PROBLEMİNİ ÇÖZEN AKILLI BAŞLATICI ---
# Klasördeki ana .runtimeconfig.json dosyasından projenin adını otomatik bulup çalıştırır.
ENTRYPOINT ["sh", "-c", "dll_name=$(ls *.runtimeconfig.json | sed 's/\\.runtimeconfig\\.json//'); exec dotnet \"$dll_name.dll\""]
