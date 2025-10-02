#!/usr/bin/env bash
set -euo pipefail

# ==== CONFIG ====
IMAGE="jferrigan/jake-server:latest"
REMOTE_USER="jake"
REMOTE_HOST="165.22.233.151"
CONTAINER_NAME="jake-srv"
PORT=8080  # container port your app listens on
export PROD_SPOTIFY_REDIRECT_URI=https://mindgobblin.com/callback
# =================

echo ">> Ensuring buildx is ready"
docker buildx create --use --name multiarch >/dev/null 2>&1 || docker buildx use multiarch

echo ">> Building & pushing multi-arch image: $IMAGE"
docker buildx build --platform linux/amd64,linux/arm64 \
  -t "$IMAGE" \
  --push .

echo ">> Deploying on $REMOTE_USER@$REMOTE_HOST"
ssh -o StrictHostKeyChecking=accept-new "$REMOTE_USER@$REMOTE_HOST" bash -s <<EOF
  set -euo pipefail
  echo ">> Logging into Docker (if needed)… (will use existing login if present)"
  # If your VPS already 'docker login'-ed once, this is a no-op. Otherwise comment this out and log in manually.
  # docker login

  echo "Port: $PORT"

  echo ">> Pulling latest image: $IMAGE"
  docker pull "$IMAGE"

  echo ">> Stopping old container (if running)"
  docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true

  echo ">> Starting new container"
  docker run -d --restart unless-stopped --name "$CONTAINER_NAME" \
    -e ASPNETCORE_URLS=http://0.0.0.0:$PORT \
    -e SPOTIFY_CLIENT_ID=$SPOTIFY_CLIENT_ID \
    -e SPOTIFY_CLIENT_SECRET=$SPOTIFY_CLIENT_SECRET  \
    -e SPOTIFY_REDIRECT_URI=$PROD_SPOTIFY_REDIRECT_URI \
    -e CR_TOKEN=$CR_TOKEN \
    -e PLACE_DATA_PATH="/data/place-board.bin" \
    -v /srv/mindgobblin/data:/data \
    -v /srv/mindgobblin/dp-keys:/app/keys \
    -p 127.0.0.1:$PORT:$PORT \
    "$IMAGE"

EOF

echo "✅ Deployed!"

