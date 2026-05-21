.PHONY: help test build up down logs ready verify preprocess publish config

DOTNET ?= $(shell if [ -x "$(HOME)/.dotnet/dotnet" ]; then echo "$(HOME)/.dotnet/dotnet"; else echo dotnet; fi)
COMPOSE ?= docker compose -f docker/docker-compose.yml
API_URL ?= http://localhost:9999
REFERENCES_GZ ?= resources/references.json.gz
OFFICIAL_REF ?= ../desafio/rinha-de-backend-2026-main/resources/references.json.gz
INDEX_OUT ?= test-data/rinha-specialist.idx
TEST_DATA ?= ../desafio/rinha-de-backend-2026-main/test/test-data.json
VERIFY_LIMIT ?=

help:
	@echo "Targets:"
	@echo "  make test        Run dotnet tests"
	@echo "  make build       Build Docker image (rinha-dotnet-aot-api:local)"
	@echo "  make up          Build + start stack"
	@echo "  make down        Stop stack and remove volumes"
	@echo "  make logs        Follow compose logs"
	@echo "  make ready       Wait until GET $(API_URL)/ready returns 200"
	@echo "  make verify      Run official dataset verifier (optional VERIFY_LIMIT=N)"
	@echo "  make preprocess  Build index locally -> $(INDEX_OUT)"
	@echo "  make publish     Native AOT publish (linux-x64)"
	@echo "  make config      Validate compose file"
	@echo ""
	@echo "Variables:"
	@echo "  DOTNET=$(DOTNET)"
	@echo "  REFERENCES_GZ=$(REFERENCES_GZ)"
	@echo "  TEST_DATA=$(TEST_DATA)"

test:
	$(DOTNET) test -c Release

build:
	@test -f "$(REFERENCES_GZ)" || ( \
		echo "Missing $(REFERENCES_GZ)."; \
		echo "Copy the official dataset, e.g.:"; \
		echo "  cp $(OFFICIAL_REF) $(REFERENCES_GZ)"; \
		exit 1 \
	)
	$(COMPOSE) build

up: build
	$(COMPOSE) up -d --force-recreate
	@$(MAKE) ready

down:
	$(COMPOSE) down -v --remove-orphans

logs:
	$(COMPOSE) logs -f

ready:
	@echo "Waiting for $(API_URL)/ready ..."
	@for i in $$(seq 1 90); do \
		if curl -sf "$(API_URL)/ready" >/dev/null; then echo "ready"; exit 0; fi; \
		sleep 1; \
	done; \
	echo "service did not become ready"; exit 1

verify:
	@test -f "$(INDEX_OUT)" || (echo "Missing $(INDEX_OUT). Run: make preprocess" && exit 1)
	@test -f "$(TEST_DATA)" || (echo "Missing $(TEST_DATA)" && exit 1)
	$(DOTNET) run --project src/Rinha.Verify -c Release -- \
		"$(INDEX_OUT)" "$(TEST_DATA)" $(VERIFY_LIMIT)

preprocess:
	@test -f "$(REFERENCES_GZ)" || (echo "Missing $(REFERENCES_GZ)" && exit 1)
	@mkdir -p "$$(dirname "$(INDEX_OUT)")"
	$(DOTNET) run --project src/Rinha.Preprocess -c Release -- \
		"$(REFERENCES_GZ)" "$(INDEX_OUT)"

publish:
	$(DOTNET) publish src/Rinha.Api/Rinha.Api.csproj -c Release -r linux-x64 \
		-p:PublishAot=true -p:StripSymbols=true

config:
	$(COMPOSE) config -q
