# A throwaway Fuseki for the contract tests when the Rig Edition image (digiwells/dwis.blackboard.fuseki,
# built in DWIS-Blackboard-Tests) is not at hand: the same Fuseki jar and the same dataset configuration,
# on the stock Temurin runtime. Not for use outside the tests; CI tests against the real image.
FROM eclipse-temurin:21-jre-alpine
ARG JENA_VERSION=6.2.0
ADD https://repo1.maven.org/maven2/org/apache/jena/jena-fuseki-server/${JENA_VERSION}/jena-fuseki-server-${JENA_VERSION}.jar /fuseki/fuseki-server.jar
COPY dwis.ttl /fuseki/config/dwis.ttl
RUN mkdir -p /fuseki/databases && chown -R 1000:1000 /fuseki
USER 1000
WORKDIR /fuseki
EXPOSE 3030
ENV JAVA_OPTIONS="-Xmx1g"
CMD ["sh", "-c", "exec java $JAVA_OPTIONS -jar fuseki-server.jar --config config/dwis.ttl"]
