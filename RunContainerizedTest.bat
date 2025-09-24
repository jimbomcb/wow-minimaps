docker build --no-cache --progress=plain -t minimap-tests -f Minimaps.Tests/Dockerfile .

docker run --rm minimap-tests