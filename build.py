import json
import os
import re
import urllib.error
import urllib.request
from datetime import datetime, timezone
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


def get_release(tag):
    token = os.environ.get("GITHUB_TOKEN", "")
    url = f"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/releases/tags/{tag}"
    req = urllib.request.Request(url)
    req.add_header("Accept", "application/vnd.github+json")
    if token:
        req.add_header("Authorization", f"token {token}")
    try:
        with urllib.request.urlopen(req) as resp:
            return json.loads(resp.read())
    except (urllib.error.URLError, json.JSONDecodeError) as e:
        print(f"Could not fetch release {tag}: {e}")
        return {}


def get_download_count(release):
    return sum(asset.get("download_count", 0) for asset in release.get("assets", []))


def get_changelog(release):
    body = (release.get("body") or "").strip()
    if not body:
        return None

    # Drop the auto-generated compare link, the plugin installer has no use for it
    body = re.sub(r"\n*\*\*Full Changelog\*\*:.*$", "", body).strip()
    return body or None


def get_published_at(release):
    published = release.get("published_at") or release.get("created_at")
    if not published:
        return None
    try:
        stamp = datetime.strptime(published, "%Y-%m-%dT%H:%M:%SZ")
        return int(stamp.replace(tzinfo=timezone.utc).timestamp())
    except ValueError:
        return None


def get_last_update(assembly_version, fallback=None):
    try:
        with open("repo.json", "r") as f:
            previous = json.load(f)
        if isinstance(previous, list) and previous:
            prev = previous[0]
            if prev.get("AssemblyVersion") == assembly_version:
                return int(prev["LastUpdate"])
    except (FileNotFoundError, json.JSONDecodeError, KeyError, TypeError, ValueError):
        pass
    return fallback if fallback is not None else int(time())


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

    release = get_release(tag)
    changelog = get_changelog(release)
    if changelog:
        manifest["Changelog"] = changelog

    manifest["DownloadCount"] = get_download_count(release)
    manifest["LastUpdate"] = get_last_update(version, get_published_at(release))

    with open("repo.json", "w") as f:
        json.dump([manifest], f, indent=4)

    dist_manifest_path = f"dist/{PLUGIN_NAME}/{PLUGIN_NAME}.json"
    if os.path.exists(dist_manifest_path):
        with open(dist_manifest_path, "w") as f:
            json.dump(manifest, f, indent=4)

    print(f"Generated repo.json for {PLUGIN_NAME} v{version}")
    print(f"Changelog: {len(changelog)} chars" if changelog else "Changelog: none found on the release")


if __name__ == "__main__":
    main()
