#!/usr/bin/env sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
LOG_DIR="$SCRIPT_DIR/.docker"

TIMELINE_LOCAL_API_PORT="${TIMELINE_LOCAL_API_PORT:-19001}"
TIMELINE_COMPOSE_PROJECT="${TIMELINE_COMPOSE_PROJECT:-timeline}"

if command -v docker >/dev/null 2>&1; then
    DOCKER_CONFIG_DIR="$LOG_DIR/docker-config"
    mkdir -p "$DOCKER_CONFIG_DIR"
    if [ ! -f "$DOCKER_CONFIG_DIR/config.json" ]; then
        printf '{}\n' > "$DOCKER_CONFIG_DIR/config.json"
    fi
    export DOCKER_CONFIG="$DOCKER_CONFIG_DIR"

    if docker info >/dev/null 2>&1; then
        docker compose \
            -f "$SCRIPT_DIR/docker-compose.yml" \
            -p "$TIMELINE_COMPOSE_PROJECT" \
            down --remove-orphans
    else
        echo "Docker engine is not running. Skipping docker compose down."
    fi
else
    echo "docker was not found. Skipping docker compose down."
fi

PID_FILE="$LOG_DIR/local-api-$TIMELINE_LOCAL_API_PORT.pid"
if [ -f "$PID_FILE" ]; then
    OLD_PID=$(cat "$PID_FILE" 2>/dev/null || true)
    if [ -n "$OLD_PID" ] && kill -0 "$OLD_PID" 2>/dev/null; then
        kill "$OLD_PID" 2>/dev/null || true
    fi
    rm -f "$PID_FILE"
fi

echo "Timeline stop requested."
