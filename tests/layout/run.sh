#!/bin/sh
# Compile et lance LayoutCheck dans un conteneur SDK .NET (sur le serveur).
#   sh tests/layout/run.sh /chemin/vers/lignes   (dossier de fichiers .raw RGB24)
set -e
DIR=$(cd "$(dirname "$0")/../.." && pwd)
ROWS=$(cd "$1" && pwd)
docker run --rm -v "$DIR":/src:ro -v "$ROWS":/rows:ro -w /tmp mcr.microsoft.com/dotnet/sdk:9.0 \
  sh -c 'cp -r /src /tmp/s && cd /tmp/s/tests/layout && dotnet run -v q -- /rows/*.raw'
