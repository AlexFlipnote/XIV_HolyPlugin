target:
	@printf "Use 'make \033[0;36mtarget\033[0m' where \033[0;36mtarget\033[0m is one of the following:\n\n"
	@awk -F ':|##' '/^[^\t].+?:.*?##/ {t[++c]=$$1; d[c]=$$NF; type[c]=1; if(length($$1)>m) m=length($$1)} /^##@/ {type[++c]=0; text[c]=substr($$0, 5)} END {for(i=1;i<=c;i++) if(type[i]==1) printf "  \033[0;36m%-*s\033[0m %s\n", m, t[i], d[i]; else {if(h++) printf "\n"; printf "\033[1m%s\033[0m\n", text[i]}}' $(MAKEFILE_LIST)

##@ Code quality
type:  ## Run pyright type checker
	@uv run pyright --pythonversion 3.11

lint:  ## Run ruff linter
	@uv run ruff check --config pyproject.toml

format:  ## Run ruff formatter
	@uv run ruff format --config pyproject.toml

##@ Sigs
scan:  ## Scan exe against all known sigs
	@uv run index.py scan

scan-custom:  ## Scan a custom exe path (make scan-custom EXE=path)
	@uv run index.py scan --exe "$(EXE)"

scan-alex:  ## I am lazy, I added my own path...
	@uv run index.py scan --exe "D:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\ffxiv_dx11.exe"

list:  ## List all tracked sigs
	@uv run index.py list

##@ Development
install:  ## Install dependencies for production
	@uv sync --no-dev

install_dev:  ## Install dev dependencies
	@uv sync

clean:  ## Clean the project
	rm -rf build dist *.egg-info .venv .ruff_cache
	rm uv.lock
