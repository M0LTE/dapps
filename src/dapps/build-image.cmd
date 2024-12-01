rem docker build -t m0lte/dapps-core .
docker buildx build --platform linux/amd64,linux/arm64 -t m0lte/dapps-core .
docker push m0lte/dapps-core