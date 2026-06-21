import json
import os
import re
import urllib.request
from time import time

REPO_OWNER = "AlexFlipnote"
REPO_NAME = "XIV_HolyPlugin"
PLUGIN_NAME = "HoliestFluffiness"

DEFAULTS = {
    "IsHide": False,
    "IsTestingExclusive": False,
    "ApplicableVersion": "any",
}


def get_version():
    tag = os.environ.get("RELEASE_TAG", "")
    return tag.lstrip("v")


def get_dalamud_api_level():
    with open(f"{PLUGIN_NAME}/{PLUGIN_NAME}.csproj", "r") as f:
        content = f.read()
    match = re.search(r"Dalamud\.NET\.Sdk/(\d+)", content)
    return int(match.group(1)) if match else 15


def get_download_count(version):
    token = os.environ.get("GITHUB_TOKEN", "")
    url = f"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/releases/tags/v{version}"
    req = urllib.request.Request(url)
    if token:
        req.add_header("Authorization", f"token {token}")
    try:
        with urllib.request.urlopen(req) as resp:
            data = json.loads(resp.read())
            return sum(asset["download_count"] for asset in data.get("assets", []))
    except urllib.error.HTTPError:
        return 0


def get_last_update(assembly_version):
    try:
        with open("repo.json", "r") as f:
            previous = json.load(f)
        if isinstance(previous, list) and previous:
            prev = previous[0]
            if prev.get("AssemblyVersion") == assembly_version:
                return prev["LastUpdate"]
    except (FileNotFoundError, json.JSONDecodeError, KeyError):
        pass
    return str(int(time()))


def main():
    version = get_version()
    if not version:
        raise ValueError("RELEASE_TAG environment variable is not set")

    with open(f"{PLUGIN_NAME}/{PLUGIN_NAME}.json", "r") as f:
        manifest = json.load(f)

    manifest["InternalName"] = PLUGIN_NAME
    manifest["AssemblyVersion"] = version
    manifest["DalamudApiLevel"] = get_dalamud_api_level()
    manifest.setdefault("IconUrl", f"https://raw.githubusercontent.com/{REPO_OWNER}/{REPO_NAME}/master/{PLUGIN_NAME}/Images/icon.png")
    manifest.setdefault("ImageUrls", [])

    for k, v in DEFAULTS.items():
        if k not in manifest:
            manifest[k] = v

    tag = os.environ.get("RELEASE_TAG", version)
    download_url = f"https://github.com/{REPO_OWNER}/{REPO_NAME}/releases/download/{tag}/latest.zip"
    manifest["DownloadLinkInstall"] = download_url
    manifest["DownloadLinkTesting"] = download_url
    manifest["DownloadLinkUpdate"] = download_url

    manifest["DownloadCount"] = get_download_count(version)
    manifest["LastUpdate"] = get_last_update(version)

    with open("repo.json", "w") as f:
        json.dump([manifest], f, indent=4)

    dist_manifest_path = f"dist/{PLUGIN_NAME}/{PLUGIN_NAME}.json"
    if os.path.exists(dist_manifest_path):
        with open(dist_manifest_path, "w") as f:
            json.dump(manifest, f, indent=4)

    print(f"Generated repo.json for {PLUGIN_NAME} v{version}")


if __name__ == "__main__":
    main()
