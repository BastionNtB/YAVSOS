#!/usr/bin/env bash
set -euo pipefail

project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd -- "$project_root"
# Release by default; YAVSOS_CONFIGURATION=Lab builds the lab fixtures in (D077 B09, lab deployments only).
dotnet build YAVSOS.sln --configuration "${YAVSOS_CONFIGURATION:-Release}" "$@"
