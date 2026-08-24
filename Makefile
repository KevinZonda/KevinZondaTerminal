SOLUTION := KevinZonda.Terminal.slnx
PROJECT := src/KevinZonda.Terminal/KevinZonda.Terminal.csproj
SERVER_PROJECT := src/KevinZonda.Terminal.Server/KevinZonda.Terminal.Server.csproj
AUTH_PROJECT := src/KevinZonda.Terminal.Server.UserAuth/KevinZonda.Terminal.Server.UserAuth.csproj
AUTH_TEST_PROJECT := tests/KevinZonda.Terminal.Server.UserAuth.Tests/KevinZonda.Terminal.Server.UserAuth.Tests.csproj
WEB_DIR := src/KevinZonda.Terminal.Web
SMOKE_TEST := scripts/smoke.ps1
SERVER_SMOKE_TEST := scripts/server-smoke.ps1
SERVER_AUTH_SMOKE_TEST := scripts/server-auth-smoke.ps1
PUBLISH_DIR := src/KevinZonda.Terminal/bin/Release/net10.0-windows/win-x64/publish
PUBLISH_EXE := $(PUBLISH_DIR)/KevinZonda.Terminal.exe
SERVER_PUBLISH_DIR := src/KevinZonda.Terminal.Server/bin/Release/net10.0-windows/win-x64/publish
SERVER_PUBLISH_EXE := $(SERVER_PUBLISH_DIR)/kterm-server.exe
AUTH_PUBLISH_DIR := src/KevinZonda.Terminal.Server.UserAuth/bin/Release/net10.0-windows/win-x64/publish
AUTH_PUBLISH_EXE := $(AUTH_PUBLISH_DIR)/kterm-server-auth.exe
INSTALL_DIR := C:/Tools/Bin
INSTALL_EXE := $(INSTALL_DIR)/zt.exe
INSTALL_SERVER_EXE := $(INSTALL_DIR)/kterm-server.exe
INSTALL_AUTH_EXE := $(INSTALL_DIR)/kterm-server-auth.exe
SERVER_URL ?= http://0.0.0.0:7132
AUTH_FILE ?=
AUTH_FILE_ARG = $(if $(strip $(AUTH_FILE)),--file "$(AUTH_FILE)",)

CONFIG ?= Debug

.DEFAULT_GOAL := build

.PHONY: help deps install restore web build run run-server auth-init auth-add auth-verify test test-desktop test-server test-server-auth test-auth format audit publish publish-desktop publish-server publish-auth clean

help:
	@echo "Available targets:"
	@echo "  make deps      - restore NuGet and pnpm dependencies"
	@echo "  make install   - install zt.exe, kterm-server.exe, and kterm-server-auth.exe"
	@echo "  make web       - type-check and build the web frontend"
	@echo "  make build     - build KevinZonda Terminal; CONFIG=Debug by default"
	@echo "  make run       - build and run KevinZonda Terminal"
	@echo "  make run-server - build and run kterm-server; SERVER_URL=http://0.0.0.0:7132"
	@echo "  make auth-init - create server_auth.json; optionally set AUTH_FILE=path"
	@echo "  make auth-add  - add an allowed password; optionally set AUTH_FILE=path"
	@echo "  make auth-verify - verify a password; optionally set AUTH_FILE=path"
	@echo "  make test      - run desktop, server, and user-auth tests"
	@echo "  make test-desktop - run the desktop 2x2 ConPTY smoke test"
	@echo "  make test-server - run the HTTP, WebSocket, and Shell server smoke test"
	@echo "  make test-server-auth - run the server Basic/cookie authentication smoke test"
	@echo "  make test-auth - run the server user-auth tests"
	@echo "  make format    - verify C# formatting"
	@echo "  make audit     - audit NuGet and pnpm dependencies"
	@echo "  make publish   - publish all ReadyToRun single-file win-x64 executables"
	@echo "  make publish-desktop - publish the desktop executable"
	@echo "  make publish-server - publish the server executable"
	@echo "  make publish-auth - publish the server user-auth executable"
	@echo "  make clean     - clean .NET build outputs"

deps: restore
	pnpm --dir $(WEB_DIR) install --frozen-lockfile

install: publish
	powershell -NoProfile -Command "New-Item -ItemType Directory -Force -Path '$(INSTALL_DIR)' | Out-Null; Copy-Item -Force -LiteralPath '$(PUBLISH_EXE)' -Destination '$(INSTALL_EXE)'; Copy-Item -Force -LiteralPath '$(SERVER_PUBLISH_EXE)' -Destination '$(INSTALL_SERVER_EXE)'; Copy-Item -Force -LiteralPath '$(AUTH_PUBLISH_EXE)' -Destination '$(INSTALL_AUTH_EXE)'"
	@echo "Installed $(INSTALL_EXE), $(INSTALL_SERVER_EXE), and $(INSTALL_AUTH_EXE)"

restore:
	dotnet restore $(SOLUTION) --nologo

web:
	pnpm --dir $(WEB_DIR) run build

build:
	dotnet build $(SOLUTION) -c $(CONFIG) --nologo

run:
	dotnet run --project $(PROJECT) -c $(CONFIG)

run-server:
	dotnet run --project $(SERVER_PROJECT) -c $(CONFIG) -- --urls $(SERVER_URL)

auth-init:
	dotnet run --project $(AUTH_PROJECT) -c $(CONFIG) -- init $(AUTH_FILE_ARG)

auth-add:
	dotnet run --project $(AUTH_PROJECT) -c $(CONFIG) -- add $(AUTH_FILE_ARG)

auth-verify:
	dotnet run --project $(AUTH_PROJECT) -c $(CONFIG) -- verify $(AUTH_FILE_ARG)

test: test-desktop test-server test-server-auth test-auth

test-desktop:
	powershell -NoProfile -ExecutionPolicy Bypass -File $(SMOKE_TEST)

test-server:
	powershell -NoProfile -ExecutionPolicy Bypass -File $(SERVER_SMOKE_TEST) -Configuration $(CONFIG)

test-server-auth:
	powershell -NoProfile -ExecutionPolicy Bypass -File $(SERVER_AUTH_SMOKE_TEST) -Configuration $(CONFIG)

test-auth:
	dotnet run --project $(AUTH_TEST_PROJECT) -c $(CONFIG) --no-launch-profile

format:
	dotnet format $(SOLUTION) --verify-no-changes --no-restore

audit:
	dotnet list $(SOLUTION) package --vulnerable --include-transitive
	pnpm --dir $(WEB_DIR) audit --audit-level high

publish: publish-desktop publish-server publish-auth

publish-desktop:
	dotnet publish $(PROJECT) -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -p:PublishSingleFile=true --nologo

publish-server:
	dotnet publish $(SERVER_PROJECT) -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -p:PublishSingleFile=true --nologo

publish-auth:
	dotnet publish $(AUTH_PROJECT) -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -p:PublishSingleFile=true --nologo

clean:
	dotnet clean $(SOLUTION) -c $(CONFIG) --nologo
