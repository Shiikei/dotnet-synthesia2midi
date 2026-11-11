#!/bin/sh
# Lance check_reference.py dans le conteneur. Exemples (sur le serveur, depuis /opt/dotnet-synthesia2midi) :
#   sh tests/reference/run.sh check all
#   sh tests/reference/run.sh freeze kimetsu <id-conversion-validée>
set -e
DIR=$(cd "$(dirname "$0")" && pwd)
docker exec dotnet-synthesia2midi mkdir -p /tmp/reference
docker cp "$DIR/check_reference.py" dotnet-synthesia2midi:/tmp/reference/check_reference.py
docker cp "$DIR/references.json" dotnet-synthesia2midi:/tmp/reference/references.json
docker exec dotnet-synthesia2midi python3 /tmp/reference/check_reference.py "$@"
