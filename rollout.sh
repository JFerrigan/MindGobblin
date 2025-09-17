export PROD_SPOTIFY_REDIRECT_URI=https://mindgobblin.com/callback

ssh jake@165.22.233.151 '
  docker rm -f jake-srv || true
  docker run -d --restart unless-stopped --name jake-srv \
    -e ASPNETCORE_URLS=http://0.0.0.0:8080 \
    -e SPOTIFY_CLIENT_ID=$SPOTIFY_CLIENT_ID \
    -e SPOTIFY_CLIENT_SECRET=$SPOTIFY_CLIENT_SECRET  \
    -e SPOTIFY_REDIRECT_URI=$PROD_SPOTIFY_REDIRECT_URI \
    -e PLACE_DATA_PATH="/data/place-board.bin" \
    -v /srv/mindgobblin/data:/data \
    -v /srv/mindgobblin/dp-keys:/app/keys \
    -p 127.0.0.1:8080:8080 \
    jferrigan/jake-server:latest
'

echo "✅ Deployed!"



