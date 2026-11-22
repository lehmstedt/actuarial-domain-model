#!/usr/bin/env bash
# Builds the static web app into ./site. Serve that folder with any static file server, e.g.
#   python3 -m http.server -d site 8080
set -euo pipefail
cd "$(dirname "$0")"
dotnet publish src/Actuarial.Web -c Release -o .publish
rm -rf site && cp -r .publish/wwwroot site && rm -rf .publish
echo "Static site written to ./site"
