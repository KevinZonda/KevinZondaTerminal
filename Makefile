SOLUTION := KevinZonda.Terminal.slnx
PROJECT := src/KevinZonda.Terminal.WinFormsDesktop/KevinZonda.Terminal.WinFormsDesktop.csproj
AVALONIA_PROJECT := src/KevinZonda.Terminal.AvaloniaDesktop/KevinZonda.Terminal.AvaloniaDesktop.csproj
SERVER_PROJECT := src/KevinZonda.Terminal.Server/KevinZonda.Terminal.Server.csproj
LAUNCHER_PROJECT := src/KevinZonda.Terminal.Server.Launcher/KevinZonda.Terminal.Server.Launcher.csproj
AUTH_TEST_PROJECT := tests/KevinZonda.Terminal.Server.UserAuth.Tests/KevinZonda.Terminal.Server.UserAuth.Tests.csproj
LAUNCHER_TEST_PROJECT := tests/KevinZonda.Terminal.Server.Launcher.Tests/KevinZonda.Terminal.Server.Launcher.Tests.csproj
UNIX_PTY_TEST_PROJECT := tests/KevinZonda.Terminal.UnixPty.Tests/KevinZonda.Terminal.UnixPty.Tests.csproj
AVALONIA_TEST_PROJECT := tests/KevinZonda.Terminal.AvaloniaDesktop.Tests/KevinZonda.Terminal.AvaloniaDesktop.Tests.csproj
WEB_DIR := src/KevinZonda.Terminal.Web
DASHBOARD_DIR := src/KevinZonda.Terminal.Server.Dashboard
SMOKE_TEST := scripts/smoke.ps1
SERVER_SMOKE_TEST := scripts/server-smoke.ps1
SERVER_AUTH_SMOKE_TEST := scripts/server-auth-smoke.ps1
SERVER_LAUNCHER_SMOKE_TEST := scripts/server-launcher-smoke.ps1
PUBLISH_DIR := src/KevinZonda.Terminal.WinFormsDesktop/bin/Release/net10.0-windows/win-x64/publish
PUBLISH_EXE := $(PUBLISH_DIR)/KevinZonda.Terminal.exe
SERVER_PUBLISH_DIR := src/KevinZonda.Terminal.Server/bin/Release/net10.0-windows/win-x64/publish
SERVER_PUBLISH_EXE := $(SERVER_PUBLISH_DIR)/kterm-server.exe
LAUNCHER_PUBLISH_DIR := src/KevinZonda.Terminal.Server.Launcher/bin/Release/net10.0-windows/win-x64/publish
LAUNCHER_PUBLISH_EXE := $(LAUNCHER_PUBLISH_DIR)/kterm-server-launcher.exe
INSTALL_DIR := C:/Tools/Bin
INSTALL_EXE := $(INSTALL_DIR)/zt.exe
INSTALL_SERVER_EXE := $(INSTALL_DIR)/kterm-server.exe
INSTALL_LAUNCHER_EXE := $(INSTALL_DIR)/kterm-server-launcher.exe
SERVER_URL ?= http://0.0.0.0:7132
AUTH_FILE ?=
AUTH_FILE_ARG = $(if $(strip $(AUTH_FILE)),--file "$(AUTH_FILE)",)
AVALONIA_ARGS ?=
HOST_OS := $(shell uname -s 2>/dev/null)
HOST_ARCH := $(shell uname -m 2>/dev/null)
ifeq ($(HOST_ARCH),x86_64)
AVALONIA_ARCH := x64
else ifeq ($(HOST_ARCH),aarch64)
AVALONIA_ARCH := arm64
else
AVALONIA_ARCH := $(HOST_ARCH)
endif
ifeq ($(HOST_OS),Darwin)
AVALONIA_RID ?= osx-$(AVALONIA_ARCH)
else ifeq ($(HOST_OS),Linux)
AVALONIA_RID ?= linux-$(AVALONIA_ARCH)
else
AVALONIA_RID ?= unsupported
endif
AVALONIA_SELF_CONTAINED ?= false
MACOS_BUNDLE_ID ?= com.kevinzonda.terminal
MACOS_SIGN_IDENTITY ?= -
MACOS_PUBLISH_TRIMMED ?= true

CONFIG ?= Debug

.DEFAULT_GOAL := build

.PHONY: help deps install restore web dashboard build build-avalonia run run-avalonia run-server run-launcher auth-init auth-add auth-verify test test-desktop test-server test-server-auth test-server-launcher test-launcher-cert test-auth test-unix-pty test-system-metrics format audit publish publish-desktop publish-avalonia app-avalonia publish-server publish-launcher clean

help:
	@echo "Available targets:"
	@echo "  make deps      - restore NuGet and pnpm dependencies"
	@echo "  make install   - install desktop, Server, and Server Launcher executables"
	@echo "  make web       - type-check and build the terminal and Dashboard frontends"
	@echo "  make dashboard - type-check and build the Server Dashboard frontend"
	@echo "  make build     - build KevinZonda Terminal; CONFIG=Debug by default"
	@echo "  make build-avalonia - build the macOS/Linux Avalonia desktop app"
	@echo "  make run       - build and run KevinZonda Terminal"
	@echo "  make run-avalonia - run the Avalonia app; optionally set AVALONIA_ARGS='--working-directory path'"
	@echo "  make run-server - build and run kterm-server; SERVER_URL=http://0.0.0.0:7132"
	@echo "  make run-launcher - build and run the Server tray Launcher"
	@echo "  make auth-init - create server_auth.json; optionally set AUTH_FILE=path"
	@echo "  make auth-add  - add an allowed password; optionally set AUTH_FILE=path"
	@echo "  make auth-verify - verify a password; optionally set AUTH_FILE=path"
	@echo "  make test      - run desktop, Server, Launcher, and user-auth tests"
	@echo "  make test-desktop - run the desktop 2x2 ConPTY smoke test"
	@echo "  make test-server - run the HTTP, WebSocket, and Shell server smoke test"
	@echo "  make test-server-auth - run the server form/cookie authentication smoke test"
	@echo "  make test-server-launcher - run the Server Launcher lifecycle smoke test"
	@echo "  make test-launcher-cert - run the Server Launcher certificate tests"
	@echo "  make test-auth - run the server user-auth tests"
	@echo "  make test-unix-pty - run the macOS/Linux PTY integration tests"
	@echo "  make test-system-metrics - run the macOS/Linux CPU and memory integration test"
	@echo "  make format    - verify C# formatting"
	@echo "  make audit     - audit NuGet and pnpm dependencies"
	@echo "  make publish   - publish all ReadyToRun single-file win-x64 executables"
	@echo "  make publish-desktop - publish the desktop executable"
	@echo "  make publish-avalonia - publish for the current host RID; override AVALONIA_RID if needed"
	@echo "  make app-avalonia - build a self-contained macOS .app for the current architecture"
	@echo "  make publish-server - publish the server executable"
	@echo "  make publish-launcher - publish the Server tray Launcher"
	@echo "  make clean     - clean .NET build outputs"

deps: restore
	pnpm --dir $(WEB_DIR) install --frozen-lockfile
	pnpm --dir $(DASHBOARD_DIR) install --frozen-lockfile

install: publish
	powershell -NoProfile -Command "New-Item -ItemType Directory -Force -Path '$(INSTALL_DIR)' | Out-Null; Copy-Item -Force -LiteralPath '$(PUBLISH_EXE)' -Destination '$(INSTALL_EXE)'; Copy-Item -Force -LiteralPath '$(SERVER_PUBLISH_EXE)' -Destination '$(INSTALL_SERVER_EXE)'; Copy-Item -Force -LiteralPath '$(LAUNCHER_PUBLISH_EXE)' -Destination '$(INSTALL_LAUNCHER_EXE)'"
	@echo "Installed $(INSTALL_EXE), $(INSTALL_SERVER_EXE), and $(INSTALL_LAUNCHER_EXE)"

restore:
	dotnet restore $(SOLUTION) --nologo

web:
	pnpm --dir $(WEB_DIR) run build
	pnpm --dir $(DASHBOARD_DIR) run build

dashboard:
	pnpm --dir $(DASHBOARD_DIR) run build

build:
	dotnet build $(SOLUTION) -c $(CONFIG) --nologo

build-avalonia:
	dotnet build $(AVALONIA_PROJECT) -c $(CONFIG) --nologo

run:
	dotnet run --project $(PROJECT) -c $(CONFIG)

run-avalonia:
	dotnet run --project $(AVALONIA_PROJECT) -c $(CONFIG) -- $(AVALONIA_ARGS)

run-server:
	dotnet run --project $(SERVER_PROJECT) -c $(CONFIG) -- --urls $(SERVER_URL)

run-launcher:
	dotnet run --project $(LAUNCHER_PROJECT) -c $(CONFIG)

auth-init:
	dotnet run --project $(SERVER_PROJECT) -c $(CONFIG) -- auth init $(AUTH_FILE_ARG)

auth-add:
	dotnet run --project $(SERVER_PROJECT) -c $(CONFIG) -- auth add $(AUTH_FILE_ARG)

auth-verify:
	dotnet run --project $(SERVER_PROJECT) -c $(CONFIG) -- auth verify $(AUTH_FILE_ARG)

test: test-desktop test-server test-server-auth test-server-launcher test-launcher-cert test-auth

test-desktop:
	powershell -NoProfile -ExecutionPolicy Bypass -File $(SMOKE_TEST)

test-server:
	powershell -NoProfile -ExecutionPolicy Bypass -File $(SERVER_SMOKE_TEST) -Configuration $(CONFIG)

test-server-auth:
	powershell -NoProfile -ExecutionPolicy Bypass -File $(SERVER_AUTH_SMOKE_TEST) -Configuration $(CONFIG)

test-server-launcher:
	powershell -NoProfile -ExecutionPolicy Bypass -File $(SERVER_LAUNCHER_SMOKE_TEST) -Configuration $(CONFIG)

test-launcher-cert:
	dotnet run --project $(LAUNCHER_TEST_PROJECT) -c $(CONFIG) --no-launch-profile

test-auth:
	dotnet run --project $(AUTH_TEST_PROJECT) -c $(CONFIG) --no-launch-profile

test-unix-pty:
	dotnet run --project $(UNIX_PTY_TEST_PROJECT) -c $(CONFIG) --no-launch-profile

test-system-metrics:
	dotnet run --project $(AVALONIA_TEST_PROJECT) -c $(CONFIG) --no-launch-profile

format:
	dotnet format $(SOLUTION) --verify-no-changes --no-restore

audit:
	dotnet list $(SOLUTION) package --vulnerable --include-transitive
	pnpm --dir $(WEB_DIR) audit --audit-level high
	pnpm --dir $(DASHBOARD_DIR) audit --audit-level high

publish: publish-desktop publish-launcher

publish-desktop:
	dotnet publish $(PROJECT) -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -p:PublishSingleFile=true --nologo

publish-avalonia:
	dotnet publish $(AVALONIA_PROJECT) -c Release -r $(AVALONIA_RID) --self-contained $(AVALONIA_SELF_CONTAINED) --nologo

app-avalonia:
	MACOS_BUNDLE_ID="$(MACOS_BUNDLE_ID)" MACOS_SIGN_IDENTITY="$(MACOS_SIGN_IDENTITY)" MACOS_PUBLISH_TRIMMED="$(MACOS_PUBLISH_TRIMMED)" scripts/package-macos.sh "$(AVALONIA_RID)"

publish-server:
	dotnet publish $(SERVER_PROJECT) -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -p:PublishSingleFile=true --nologo

publish-launcher: publish-server
	dotnet publish $(LAUNCHER_PROJECT) -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -p:PublishSingleFile=true --nologo
	powershell -NoProfile -Command "Copy-Item -Force -LiteralPath '$(SERVER_PUBLISH_EXE)' -Destination '$(LAUNCHER_PUBLISH_DIR)/kterm-server.exe'"

clean:
	dotnet clean $(SOLUTION) -c $(CONFIG) --nologo
