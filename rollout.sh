ssh jake@165.22.233.151 '
  docker rm -f jake-srv || true
  docker run -d --restart unless-stopped --name jake-srv \
    -e ASPNETCORE_URLS=http://0.0.0.0:8080 \
    -p 127.0.0.1:8080:8080 \
    jferrigan/jake-server:latest
'

echo "✅ Deployed!"



