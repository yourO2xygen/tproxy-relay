# Official MTProxy at pinned commit f36d8af769ffaeac36978d38c2c0f6d1104c2137,
# built unchanged (x86_64, glibc required).
FROM debian:12 AS build
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential libssl-dev zlib1g-dev curl ca-certificates make rsync \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
RUN curl -fsSL --retry 5 --retry-delay 5 -o mtproxy.tar.gz \
        https://codeload.github.com/TelegramMessenger/MTProxy/tar.gz/f36d8af769ffaeac36978d38c2c0f6d1104c2137 \
    && tar xzf mtproxy.tar.gz \
    && mv MTProxy-f36d8af769ffaeac36978d38c2c0f6d1104c2137 MTProxy \
    && cd MTProxy \
    && make -j"$(nproc)"

FROM debian:12-slim
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /src/MTProxy/objs/bin/mtproto-proxy /usr/local/bin/mtproto-proxy
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
EXPOSE 2398 8888
ENTRYPOINT ["/entrypoint.sh"]
