# SettleFlow developer commands. Run `make` or `make help` to list them.
.DEFAULT_GOAL := help
SHELL := /bin/bash

RATE ?= 50
DURATION ?= 60s

.PHONY: help
help: ## List commands
	@grep -E '^[a-zA-Z0-9_-]+:.*?## ' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-14s\033[0m %s\n", $$1, $$2}'

.PHONY: up
up: ## Build and start the whole stack in the background
	docker compose up --build -d
	@echo "Dashboard http://localhost:3000 · API http://localhost:8083/swagger · Grafana http://localhost:3001"

.PHONY: down
down: ## Stop the stack (keeps data)
	docker compose down

.PHONY: reset
reset: ## Stop the stack AND delete all local data (asks first)
	@read -p "This deletes the local database, Kafka data and Grafana state. Type 'yes' to continue: " ok && [ "$$ok" = "yes" ]
	docker compose down -v

.PHONY: logs
logs: ## Follow logs of the .NET services
	docker compose logs -f api fraud-worker settlement-worker notification-worker

.PHONY: build
build: ## Build backend (warnings as errors, like CI) and frontend
	dotnet build SettleFlow.sln -c Release -warnaserror
	cd frontend && npm ci && npm run build

.PHONY: test
test: test-unit test-e2e ## Run backend unit + e2e tests and frontend unit tests
	cd frontend && npm test

.PHONY: test-unit
test-unit: ## Unit tests (no Docker needed)
	dotnet test tests/SettleFlow.Tests

.PHONY: test-e2e
test-e2e: ## End-to-end tests against real Postgres + Redpanda (Testcontainers; needs Docker)
	dotnet test tests/SettleFlow.IntegrationTests

.PHONY: test-ui
test-ui: ## Playwright browser tests against the running stack (uses local Chrome)
	cd frontend && npm run test:e2e

COUNT ?= 150

.PHONY: seed
seed: ## Fill the running stack with realistic demo data (COUNT=150)
	docker run --rm --network settleflow_default -v "$(CURDIR)/loadtest:/scripts" grafana/k6:1.3.0 \
		run -q -e BASE_URL=http://api:8080 -e COUNT=$(COUNT) /scripts/seed.js

.PHONY: format
format: ## Format backend (dotnet format) and frontend (Prettier)
	dotnet format whitespace SettleFlow.sln
	cd frontend && npm run format

.PHONY: lint
lint: ## Check formatting, types and the API contract without changing files (what CI runs)
	dotnet format whitespace SettleFlow.sln --verify-no-changes
	cd frontend && npm run format:check && npm run typecheck && npm run check:contract

.PHONY: load-test
load-test: ## k6 load test against the running stack (RATE=50 DURATION=60s)
	docker run --rm --network settleflow_default -v "$(CURDIR)/loadtest:/scripts" grafana/k6:1.3.0 \
		run -e BASE_URL=http://api:8080 -e RATE=$(RATE) -e DURATION=$(DURATION) /scripts/payments.js

.PHONY: migration
migration: ## Add an EF Core migration: make migration name=AddSomething
	@test -n "$(name)" || (echo "usage: make migration name=MigrationName" && exit 1)
	dotnet tool restore
	dotnet dotnet-ef migrations add $(name) --project src/SettleFlow.Infrastructure --startup-project src/SettleFlow.Infrastructure --output-dir Migrations

.PHONY: dev-frontend
dev-frontend: ## Vite dev server with hot reload, proxied to the API on :8083
	cd frontend && npm install && npm run dev
