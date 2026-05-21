FROM mcr.microsoft.com/dotnet/sdk:11.0-preview AS build
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev && rm -rf /var/lib/apt/lists/*
WORKDIR /src

COPY global.json Directory.Build.props Rinha.slnx ./
COPY src/Rinha.Api/Rinha.Api.csproj src/Rinha.Api/
COPY src/Rinha.Preprocess/Rinha.Preprocess.csproj src/Rinha.Preprocess/
RUN dotnet restore src/Rinha.Preprocess/Rinha.Preprocess.csproj && \
    dotnet restore src/Rinha.Api/Rinha.Api.csproj -r linux-x64 -p:PublishAot=true

COPY src/ ./src/

RUN dotnet restore src/Rinha.Preprocess/Rinha.Preprocess.csproj && \
    dotnet restore src/Rinha.Api/Rinha.Api.csproj -r linux-x64 -p:PublishAot=true

ARG REFERENCES_GZ=/src/references.json.gz
ARG RINHA_LEAF_SIZE=48
ENV RINHA_LEAF_SIZE=${RINHA_LEAF_SIZE}
COPY resources/references.json.gz* ${REFERENCES_GZ}

RUN dotnet run --project src/Rinha.Preprocess/Rinha.Preprocess.csproj -c Release --no-restore -- \
    ${REFERENCES_GZ} /out/index/rinha-specialist.idx

RUN dotnet publish src/Rinha.Api/Rinha.Api.csproj -c Release -o /out/app \
    -r linux-x64 \
    -p:PublishAot=true \
    -p:StripSymbols=true \
    -p:IlcInstructionSet=avx2 \
    --no-restore

FROM mcr.microsoft.com/dotnet/runtime-deps:11.0-preview AS final
WORKDIR /app
COPY --from=build /out/app/ ./
COPY --from=build /out/index/ ./index/
ENV RINHA_INDEX_PATH=/app/index/rinha-specialist.idx
ENV RINHA_SEARCH_MODE=key-first
ENTRYPOINT ["/app/Rinha.Api"]
