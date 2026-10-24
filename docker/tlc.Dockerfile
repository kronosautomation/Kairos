# TLA+ model checker (TLC) for spec/. Pinned to tla2tools v1.7.4, verified against the published SHA-1.
FROM eclipse-temurin:21-jre
ARG TLA_VERSION=v1.7.4
ARG TLA_SHA1=bee4a54f3ee3d4afc347c3240ec2d9e93b075104
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates && rm -rf /var/lib/apt/lists/* \
 && curl -fsSL "https://github.com/tlaplus/tlaplus/releases/download/${TLA_VERSION}/tla2tools.jar" -o /opt/tla2tools.jar \
 && echo "${TLA_SHA1}  /opt/tla2tools.jar" | sha1sum -c -
ENV TLA2TOOLS=/opt/tla2tools.jar
# The repo is mounted read-only; TLC writes state files, so check a copy of spec/.
ENTRYPOINT ["bash", "-c", "rm -rf /work && mkdir -p /work && cp -r /src/spec /work/spec && bash /work/spec/check.sh 2>&1 | tee /results/tlc-$(date +%Y%m%d-%H%M%S).log; exit ${PIPESTATUS[0]}"]
