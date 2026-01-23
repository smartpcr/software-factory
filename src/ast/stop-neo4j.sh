#!/bin/bash
# Stop Neo4j and Qdrant containers

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

echo "Stopping Neo4j and Qdrant containers..."
docker compose down

echo "Containers stopped."
