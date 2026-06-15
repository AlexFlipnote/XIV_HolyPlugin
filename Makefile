PLUGIN     := HoliestFluffiness
PROJ       := $(PLUGIN)/$(PLUGIN).csproj
OUT_DEBUG  := $(PLUGIN)/bin/x64/Debug
OUT_REL    := $(PLUGIN)/bin/x64/Release
DIST       := dist/$(PLUGIN)

.PHONY: build release pack clean

build:
	dotnet build $(PROJ)

release:
	dotnet build $(PROJ) -c Release

pack: release
	mkdir -p $(DIST)
	cp $(OUT_REL)/$(PLUGIN).dll  $(DIST)/
	cp $(OUT_REL)/$(PLUGIN).pdb  $(DIST)/ 2>/dev/null || true
	cp $(PLUGIN)/$(PLUGIN).json  $(DIST)/
	mkdir -p $(DIST)/images
	cp $(PLUGIN)/images/icon.png $(DIST)/images/

clean:
	dotnet clean $(PROJ)
	rm -rf dist/$(PLUGIN)
