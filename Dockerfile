# syntax=docker/dockerfile:1

# ---- Build ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/Synthesia2Midi/*.csproj ./Synthesia2Midi/
RUN dotnet restore ./Synthesia2Midi/Synthesia2Midi.csproj
COPY src/Synthesia2Midi/. ./Synthesia2Midi/
RUN dotnet publish ./Synthesia2Midi/Synthesia2Midi.csproj -c Release -o /app/publish

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# ffmpeg (décodage frames) + yt-dlp (téléchargement) + xvfb (display virtuel pour MuseScore headless)
# libopengl0/libegl1/libxkbcommon0 : nécessaires en plus de libgl1 pour éviter un bug connu
# de l'AppImage MuseScore ("libOpenGL.so.0: cannot open shared object file",
# cf. https://github.com/musescore/MuseScore/issues/24228).
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update -o Acquire::Retries=3 && apt-get install -y --no-install-recommends \
        ffmpeg \
        python3 \
        python3-pip \
        xvfb \
        xauth \
        libnss3 libxcomposite1 libxrandr2 libxi6 libgl1 \
        libopengl0 libegl1 libxkbcommon0 \
        wget ca-certificates \
    && pip3 install --break-system-packages --no-cache-dir yt-dlp \
    && rm -rf /var/lib/apt/lists/*

# --- MuseScore Studio (ex-"MuseScore 4") headless ---
# ATTENTION : https://musescore.org/en/download/musescore-x86_64.AppImage sert une page HTML
# intermédiaire ("votre téléchargement va commencer dans 3s…", redirection déclenchée en JS
# côté navigateur) et NON le binaire — inutilisable avec wget. Le vrai fichier est servi par
# jsDelivr, dont le lien direct est donné sur cette page HTML elle-même. Confirmé le
# 2026-08-23 (fichier binaire, >30 Mo, SHA256 fourni sur la page de téléchargement).
# Inconvénient : ce lien est versionné (v4.7.4) et devra être mis à jour manuellement à
# chaque nouvelle release stable — va chercher le nouveau lien direct sur
# https://musescore.org/en/download (section Linux) après un `wget` qui échoue.
ARG MUSESCORE_APPIMAGE_URL=https://cdn.jsdelivr.net/musescore/v4.7.4/MuseScore-Studio-4.7.4.260706075-x86_64.AppImage
RUN wget -q -O /tmp/musescore.AppImage "$MUSESCORE_APPIMAGE_URL" \
    && chmod +x /tmp/musescore.AppImage \
    && cd /tmp && ./musescore.AppImage --appimage-extract > /dev/null \
    && mv squashfs-root /opt/musescore \
    && ln -s /opt/musescore/AppRun /usr/local/bin/mscore4 \
    && rm /tmp/musescore.AppImage

COPY --from=build /app/publish .

ENV DATA_DIR=/data
VOLUME ["/data", "/certs"]
EXPOSE 8080 8443

# Xvfb en arrière-plan (une seule fois, persistant) plutôt que xvfb-run : xvfb-run est
# conçu pour wrapper une commande ponctuelle qui se termine, pas un serveur qui tourne en
# continu — utilisé comme ENTRYPOINT d'un serveur web, il bloque/avale les logs de façon
# silencieuse (observé en homelab : le process ne bindait jamais son port, zéro log).
# -ac désactive le contrôle d'accès X11 : pas besoin de xauth, conteneur mono-usage,
# aucun client X externe.
ENV DISPLAY=:99
ENTRYPOINT ["/bin/sh", "-c", "rm -f /tmp/.X99-lock /tmp/.X11-unix/X99; Xvfb :99 -screen 0 1024x768x16 -ac -nolisten tcp & exec dotnet Synthesia2Midi.dll"]
