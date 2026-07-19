PLUGIN     := HoliestFluffiness
PROJ       := $(PLUGIN)/$(PLUGIN).csproj
OUT_DEBUG  := $(PLUGIN)/bin/x64/Debug
OUT_REL    := $(PLUGIN)/bin/x64/Release
DIST       := dist/$(PLUGIN)

.PHONY: all build lint release pack clean scan check

# Default target: a lint-enforced build. `make` alone fails on style violations (unused usings, etc.),
# so the standard stays green without anyone remembering to run a separate step.
all: lint

# Fast iteration build without lint enforcement, for tight edit/build loops.
build:
	dotnet build $(PROJ)

lint:
	dotnet build $(PROJ) --no-incremental /p:EnforceCodeStyleInBuild=true /p:GenerateDocumentationFile=true /p:NoWarn=1591 /p:WarningsAsErrors=IDE0005

release:
	dotnet build $(PROJ) -c Release

pack: release
	mkdir -p $(DIST)
	cp $(OUT_REL)/$(PLUGIN).dll  $(DIST)/
	cp $(OUT_REL)/$(PLUGIN).pdb  $(DIST)/ 2>/dev/null || true
	cp $(PLUGIN)/$(PLUGIN).json  $(DIST)/
	mkdir -p $(DIST)/Images
	cp $(PLUGIN)/Images/icon.png $(DIST)/Images/

clean:
	dotnet clean $(PROJ)
	rm -rf dist/$(PLUGIN)

scan:
	cd SigTracker && make scan-alex

check:
	cd SigTracker && make check
