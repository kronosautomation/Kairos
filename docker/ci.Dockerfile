# Local CI runner: .NET 10 SDK + .NET 11 SDK (preview/RC), matching azure-pipelines.yml.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2
RUN apt-get update && apt-get install -y --no-install-recommends rsync && rm -rf /var/lib/apt/lists/* \
 && curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && bash /tmp/dotnet-install.sh --channel 11.0 --quality preview --install-dir /usr/share/dotnet \
 && rm /tmp/dotnet-install.sh \
 && dotnet --list-sdks
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 CI=true
COPY ci-run.sh /usr/local/bin/ci-run.sh
RUN chmod +x /usr/local/bin/ci-run.sh
ENTRYPOINT ["/usr/local/bin/ci-run.sh"]
