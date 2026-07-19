PLUGIN     := HoliestFluffiness
PROJ       := $(PLUGIN)/$(PLUGIN).csproj
OUT_REL    := $(PLUGIN)/bin/Release
DIST       := dist/$(PLUGIN)

# Runtime dependencies that must ship alongside the plugin dll. SoundEngine hard-depends
# on these, so a package without them silently loses every sound feature.
RUNTIME_DLLS := NAudio.Core.dll NAudio.Wasapi.dll NAudio.Vorbis.dll NVorbis.dll

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

# Must stay in step with the Pack Plugin step in .github/workflows/release.yml.
pack: release
	mkdir -p $(DIST)
	cp $(OUT_REL)/$(PLUGIN).dll  $(DIST)/
	cp $(OUT_REL)/$(PLUGIN).pdb  $(DIST)/ 2>/dev/null || true
	for dll in $(RUNTIME_DLLS); do cp $(OUT_REL)/$$dll $(DIST)/; done
	cp $(PLUGIN)/$(PLUGIN).json  $(DIST)/
	if [ -d $(PLUGIN)/Images ]; then cp -r $(PLUGIN)/Images $(DIST)/Images; fi
	if [ -d $(PLUGIN)/Sounds ]; then cp -r $(PLUGIN)/Sounds $(DIST)/Sounds; fi

clean:
	dotnet clean $(PROJ)
	rm -rf dist/$(PLUGIN)

# Uses SigTracker's own default install path. For a non-default install:
#   cd SigTracker && make scan-custom EXE="path/to/ffxiv_dx11.exe"
scan:
	cd SigTracker && make scan

check:
	cd SigTracker && make check
