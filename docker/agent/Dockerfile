# Self-hosted Azure Pipelines agent for the Kairos-Local pool. Based on Microsoft's documented approach
# (https://learn.microsoft.com/azure/devops/pipelines/agents/docker); start.sh is our own implementation.
FROM ubuntu:24.04@sha256:68ee22004a9109b44973313b02ff7b1de250f1de0f5ecb628d73f579aaff33ac
ENV TARGETARCH=linux-x64 DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get upgrade -y \
 && apt-get install -y --no-install-recommends curl ca-certificates git jq libicu74 rsync \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /azp
COPY start.sh .
RUN chmod +x start.sh
ENTRYPOINT ["./start.sh"]
