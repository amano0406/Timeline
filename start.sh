#!/usr/bin/env sh
set -eu

NO_OPEN=0
while [ "$#" -gt 0 ]; do
    case "$1" in
        --no-open|--NoOpen|-NoOpen)
            NO_OPEN=1
            ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 2
            ;;
    esac
    shift
done

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
LOG_DIR="$SCRIPT_DIR/.docker"
LOCAL_ROOT="$SCRIPT_DIR/.local"

TIMELINE_WEB_PORT="${TIMELINE_WEB_PORT:-19000}"
TIMELINE_LOCAL_API_PORT="${TIMELINE_LOCAL_API_PORT:-19001}"
TIMELINE_OLLAMA_PORT="${TIMELINE_OLLAMA_PORT:-11434}"
TIMELINE_IMAGE_TAG="${TIMELINE_IMAGE_TAG:-latest}"
TIMELINE_OLLAMA_VOLUME_NAME="${TIMELINE_OLLAMA_VOLUME_NAME:-timeline-ollama}"
TIMELINE_COMPOSE_PROJECT="${TIMELINE_COMPOSE_PROJECT:-timeline}"
TIMELINE_DATA_ROOT="${TIMELINE_DATA_ROOT:-$SCRIPT_DIR/data}"
TIMELINE_WORK_SOURCE="${TIMELINE_WORK_SOURCE:-$TIMELINE_DATA_ROOT/work}"
TIMELINE_STORE_SOURCE="${TIMELINE_STORE_SOURCE:-$TIMELINE_DATA_ROOT/to_timeline}"

require_command() {
    if ! command -v "$1" >/dev/null 2>&1; then
        echo "$1 was not found." >&2
        exit 1
    fi
}

wait_http() {
    url="$1"
    label="$2"
    attempts="$3"
    i=1
    while [ "$i" -le "$attempts" ]; do
        if command -v curl >/dev/null 2>&1 && curl -fsS "$url" >/dev/null 2>&1; then
            return 0
        fi
        sleep 1
        i=$((i + 1))
    done
    echo "$label did not become ready: $url" >&2
    return 1
}

open_url() {
    url="$1"
    if [ "$NO_OPEN" -ne 0 ]; then
        return 0
    fi

    case "$(uname -s 2>/dev/null || echo unknown)" in
        Darwin)
            open "$url" >/dev/null 2>&1 || true
            ;;
        Linux)
            if command -v xdg-open >/dev/null 2>&1; then
                xdg-open "$url" >/dev/null 2>&1 || true
            fi
            ;;
    esac
}

require_command dotnet
require_command docker

mkdir -p "$LOG_DIR" "$LOCAL_ROOT" "$TIMELINE_DATA_ROOT" "$TIMELINE_WORK_SOURCE" "$TIMELINE_STORE_SOURCE"

BUILD_DIR="$LOCAL_ROOT/local-api-build-$TIMELINE_LOCAL_API_PORT"
PID_FILE="$LOG_DIR/local-api-$TIMELINE_LOCAL_API_PORT.pid"
STDOUT_LOG="$LOG_DIR/local-api-$TIMELINE_LOCAL_API_PORT.stdout.log"
STDERR_LOG="$LOG_DIR/local-api-$TIMELINE_LOCAL_API_PORT.stderr.log"
PUBLISH_LOG="$LOG_DIR/local-api-$TIMELINE_LOCAL_API_PORT.publish.log"

if [ -f "$PID_FILE" ]; then
    OLD_PID=$(cat "$PID_FILE" 2>/dev/null || true)
    if [ -n "$OLD_PID" ] && kill -0 "$OLD_PID" 2>/dev/null; then
        kill "$OLD_PID" 2>/dev/null || true
        sleep 1
    fi
    rm -f "$PID_FILE"
fi

rm -rf "$BUILD_DIR"
mkdir -p "$BUILD_DIR"
dotnet publish "$SCRIPT_DIR/local-api/Timeline.LocalApi.csproj" \
    -c Release \
    -p:UseAppHost=false \
    -o "$BUILD_DIR" >"$PUBLISH_LOG" 2>&1

dotnet "$BUILD_DIR/Timeline.LocalApi.dll" \
    --urls "http://127.0.0.1:$TIMELINE_LOCAL_API_PORT" \
    "--Timeline:WebPort=$TIMELINE_WEB_PORT" \
    "--Timeline:ProductPath=$SCRIPT_DIR" >"$STDOUT_LOG" 2>"$STDERR_LOG" &
echo "$!" > "$PID_FILE"

wait_http "http://127.0.0.1:$TIMELINE_LOCAL_API_PORT/health" "Timeline local API" 120

DOCKER_CONFIG_DIR="$LOG_DIR/docker-config"
mkdir -p "$DOCKER_CONFIG_DIR"
if [ ! -f "$DOCKER_CONFIG_DIR/config.json" ]; then
    printf '{}\n' > "$DOCKER_CONFIG_DIR/config.json"
fi

export DOCKER_CONFIG="$DOCKER_CONFIG_DIR"
export TIMELINE_LOCAL_API_PORT
export TIMELINE_WEB_PORT
export TIMELINE_OLLAMA_PORT
export TIMELINE_IMAGE_TAG
export TIMELINE_OLLAMA_VOLUME_NAME
export TIMELINE_WORK_SOURCE
export TIMELINE_STORE_SOURCE

docker volume inspect "$TIMELINE_OLLAMA_VOLUME_NAME" >/dev/null 2>&1 \
    || docker volume create "$TIMELINE_OLLAMA_VOLUME_NAME" >/dev/null

docker compose \
    -f "$SCRIPT_DIR/docker-compose.yml" \
    -p "$TIMELINE_COMPOSE_PROJECT" \
    up -d --build --remove-orphans ollama web worker

wait_http "http://127.0.0.1:$TIMELINE_WEB_PORT/api/health" "Timeline web" 120

WEB_URL="http://127.0.0.1:$TIMELINE_WEB_PORT"
echo ""
echo "Timeline is running."
echo "Web UI:"
echo "  $WEB_URL"
echo "Local API:"
echo "  http://127.0.0.1:$TIMELINE_LOCAL_API_PORT"
open_url "$WEB_URL"
