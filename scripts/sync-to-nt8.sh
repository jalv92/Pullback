#!/usr/bin/env bash
# Copies Pullback.cs into the NT8 Custom/Strategies folder (WSL → Windows).
# Run manually or via the post-commit hook. NT8 Editor recompile (F5) picks it up.
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")/.."
DEST="/mnt/c/Users/javlo/Documents/NinjaTrader 8/bin/Custom/Strategies"
cp Pullback.cs "$DEST/Pullback.cs"
echo "[sync-to-nt8] Pullback.cs -> $DEST"
