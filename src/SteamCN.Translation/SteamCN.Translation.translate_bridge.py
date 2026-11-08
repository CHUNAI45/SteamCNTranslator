#!/usr/bin/env python3
"""SteamCN Translator - Argos Translate Bridge

Long-lived subprocess communicating with the C# host via
line-delimited JSON on stdin/stdout.

Protocol (each message is a single JSON line):
  Request:  {"id": "...", "action": "...", ...}
  Response: {"id": "...", "success": true/false, ...}

Actions:
  init            - Load argostranslate, report installed packages
  translate       - Translate texts: {"texts": [...], "from": "en", "to": "zh"}
  install_package - Download+install language package (auto-pivot via en if needed)
  list_packages   - List installed packages
  check_health    - Verify bridge + argostranslate + packages integrity
  ping            - Health check
"""

import sys
import os
import json
import io
import hashlib
import traceback

# Force UTF-8 I/O on Windows
sys.stdin = io.TextIOWrapper(sys.stdin.buffer, encoding="utf-8")
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", line_buffering=True)
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8")

# Global state
_translators = {}       # key: "from->to", value: translator or list (pivot chain)
_initialized = False
_pivot_info = {}        # key: "from->to", value: list of pivot steps


def log_err(msg):
    """Log to stderr (C# host reads stderr for diagnostics)."""
    try:
        sys.stderr.write(f"[bridge] {msg}\n")
        sys.stderr.flush()
    except Exception:
        pass


def send_response(resp):
    """Send a JSON response line to stdout."""
    try:
        sys.stdout.write(json.dumps(resp, ensure_ascii=False) + "\n")
        sys.stdout.flush()
    except Exception as e:
        log_err(f"Failed to send response: {e}")


def ensure_initialized():
    """Lazy-import and initialize argostranslate."""
    global _initialized
    if _initialized:
        return True
    try:
        import argostranslate.package
        import argostranslate.translate
        _initialized = True
        return True
    except ImportError as e:
        log_err(f"argostranslate not installed: {e}")
        return False
    except Exception as e:
        log_err(f"init error: {e}\n{traceback.format_exc()}")
        return False


def get_installed_packages():
    """Return list of installed language packages as 'from->to' strings."""
    if not ensure_initialized():
        return []
    try:
        import argostranslate.package
        pkgs = argostranslate.package.get_installed_packages()
        return [f"{p.from_code}->{p.to_code}" for p in pkgs]
    except Exception as e:
        log_err(f"list packages error: {e}")
        return []


def find_translation_path(from_code, to_code):
    """Find a translation path: direct or pivot through English.

    Returns (list_of_steps, path_type) where:
      - path_type: "direct", "pivot_en", or "none"
      - steps: list of (from, to) tuples, e.g. [("ja","en"),("en","zh")]
    """
    if not ensure_initialized():
        return None, "none"

    import argostranslate.package
    installed = argostranslate.package.get_installed_packages()

    # Try direct
    direct = [p for p in installed
              if p.from_code == from_code and p.to_code == to_code]
    if direct:
        return [(from_code, to_code)], "direct"

    # Try pivot through English
    if from_code != "en" and to_code != "en":
        to_en = [p for p in installed
                 if p.from_code == from_code and p.to_code == "en"]
        en_to = [p for p in installed
                 if p.from_code == "en" and p.to_code == to_code]
        if to_en and en_to:
            return [(from_code, "en"), ("en", to_code)], "pivot_en"

    return None, "none"


def get_translator(from_code, to_code):
    """Get or create a translator (or pivot chain) for the language pair."""
    key = f"{from_code}->{to_code}"
    if key in _translators:
        return _translators[key]

    if not ensure_initialized():
        return None

    try:
        import argostranslate.translate

        steps, path_type = find_translation_path(from_code, to_code)
        if steps is None:
            log_err(f"No translation path found for {key}")
            return None

        chain = []
        for fc, tc in steps:
            t = argostranslate.translate.get_translation_from_codes(fc, tc)
            if t is None:
                log_err(f"Failed to load translator for {fc}->{tc}")
                return None
            chain.append(t)

        _pivot_info[key] = {
            "type": path_type,
            "steps": [f"{fc}->{tc}" for fc, tc in steps],
        }

        if len(chain) == 1:
            _translators[key] = chain[0]
            log_err(f"Loaded direct translator for {key}")
        else:
            _translators[key] = chain
            log_err(f"Loaded pivot translator for {key} via {path_type}")

        return _translators[key]
    except Exception as e:
        log_err(f"get_translator({key}) error: {e}\n{traceback.format_exc()}")
        return None


def translate_text(translator, text):
    """Translate a single text using a translator or pivot chain."""
    if isinstance(translator, list):
        # Pivot chain: translate through each step
        result = text
        for t in translator:
            result = t.translate(result)
            if result is None:
                return None
        return result
    else:
        return translator.translate(text)


def handle_init(req):
    """Initialize and report installed packages."""
    ok = ensure_initialized()
    packages = get_installed_packages() if ok else []
    return {
        "success": ok,
        "packages": packages,
        "error": None if ok else "argostranslate not installed",
        "error_type": None if ok else "ArgosNotInstalled",
    }


def handle_translate(req):
    """Translate a batch of texts."""
    texts = req.get("texts", [])
    from_code = req.get("from", "en")
    to_code = req.get("to", "zh")

    # Map common codes: zh-Hans -> zh
    from_code = from_code.split("-")[0].lower() if from_code else "en"
    to_code = to_code.split("-")[0].lower() if to_code else "zh"

    if not texts:
        return {"success": True, "translations": []}

    translator = get_translator(from_code, to_code)
    if translator is None:
        pkg_key = f"{from_code}->{to_code}"
        installed = get_installed_packages()

        # Check if pivot packages are available
        steps, path_type = find_translation_path(from_code, to_code)
        if steps is None:
            if from_code != "en" and to_code != "en":
                # Check what's missing for pivot
                has_to_en = any(p.startswith(f"{from_code}->en") for p in installed)
                has_en_to = any(p.startswith(f"en->{to_code}") for p in installed)
                missing = []
                if not has_to_en:
                    missing.append(f"{from_code}->en")
                if not has_en_to:
                    missing.append(f"en->{to_code}")
                return {
                    "success": False,
                    "error": (f"Language package {from_code}->{to_code} not installed. "
                              f"Direct package not found. Pivot via English requires: "
                              f"{', '.join(missing)}. Please install the missing packages."),
                    "error_type": "PackageNotInstalled",
                }
            return {
                "success": False,
                "error": f"Language package {from_code}->{to_code} not installed. "
                         f"Please install it in Settings first.",
                "error_type": "PackageNotInstalled",
            }
        return {
            "success": False,
            "error": f"Failed to load translator for {from_code}->{to_code}",
            "error_type": "ModelLoadFailed",
        }

    translations = []
    errors = []
    for text in texts:
        try:
            result = translate_text(translator, text)
            translations.append(result if result is not None else "")
        except Exception as e:
            log_err(f"translate error for '{text[:50]}': {e}")
            translations.append("")
            errors.append(str(e))

    return {
        "success": len(errors) == 0,
        "translations": translations,
        "error": "; ".join(errors) if errors else None,
        "error_type": "TranslationFailed" if errors else None,
    }


def verify_file_integrity(file_path, min_size=1000):
    """Verify a downloaded file is not empty or corrupted."""
    if not os.path.exists(file_path):
        return False, "File does not exist"
    size = os.path.getsize(file_path)
    if size < min_size:
        return False, f"File too small ({size} bytes, expected >= {min_size})"
    # Read first bytes to verify it's a valid file (not an error page)
    with open(file_path, "rb") as f:
        header = f.read(4)
    if header[:2] == b"<h" or header[:2] == b"<H":
        # Looks like an HTML error page
        return False, "File appears to be an HTML error page, not a valid package"
    # Compute SHA256 for logging
    sha256 = hashlib.sha256()
    with open(file_path, "rb") as f:
        for chunk in iter(lambda: f.read(8192), b""):
            sha256.update(chunk)
    log_err(f"Package file verified: {file_path} ({size} bytes, sha256={sha256.hexdigest()[:16]}...)")
    return True, f"OK ({size} bytes)"


def find_local_package(local_dir, from_code, to_code):
    """Find a pre-downloaded .argosmodel file in local_dir for the given pair.

    File naming convention: translate-{from}_{to}-{version}.argosmodel
    Returns the full path if found, else None.
    """
    if not local_dir or not os.path.isdir(local_dir):
        return None
    prefix = f"translate-{from_code}_{to_code}-"
    try:
        for fname in sorted(os.listdir(local_dir)):
            if fname.startswith(prefix) and fname.endswith(".argosmodel"):
                return os.path.join(local_dir, fname)
    except Exception as e:
        log_err(f"find_local_package error scanning {local_dir}: {e}")
    return None


def install_local_package(local_path, pkg_key):
    """Verify and install a package from a local .argosmodel file.

    Returns (True, "") on success or (False, error_message_with_type) on failure.
    """
    log_err(f"Using local package file for {pkg_key}: {local_path}")

    # Verify file integrity
    ok, verify_msg = verify_file_integrity(local_path)
    if not ok:
        return False, f"Package {pkg_key} verification failed: {verify_msg}|PackageCorrupted"

    # Install
    try:
        import argostranslate.package
        argostranslate.package.install_from_path(local_path)
    except Exception as e:
        log_err(f"Install failed for {pkg_key} from local file: {e}")
        return False, f"Failed to install package {pkg_key}: {e}|ModelLoadFailed"

    log_err(f"Package {pkg_key} installed successfully from local file")
    return True, ""


def handle_install_package(req):
    """Install a language package.

    Priority: pre-downloaded local .argosmodel files (offline) > online download.
    If no direct package exists, automatically installs pivot packages
    through English (from->en and en->to).
    """
    from_code = req.get("from", "")
    to_code = req.get("to", "")
    local_dir = req.get("local_packages_dir", "")

    if not from_code or not to_code:
        return {"success": False, "error": "Missing from/to language codes",
                "error_type": "InvalidRequest"}

    if not ensure_initialized():
        return {"success": False, "error": "argostranslate not installed",
                "error_type": "ArgosNotInstalled"}

    try:
        import argostranslate.package

        # ---- Phase 1: Try offline installation from local pre-downloaded files ----
        local_pairs = []   # list of (from, to, local_path)
        pivot = False
        pivot_note = ""

        direct_local = find_local_package(local_dir, from_code, to_code)
        if direct_local:
            local_pairs = [(from_code, to_code, direct_local)]
            log_err(f"Found local direct package: {from_code}->{to_code}")
        else:
            # Check for pivot through English locally
            if from_code != "en" and to_code != "en":
                to_en_local = find_local_package(local_dir, from_code, "en")
                en_to_local = find_local_package(local_dir, "en", to_code)
                if to_en_local and en_to_local:
                    local_pairs = [(from_code, "en", to_en_local),
                                   ("en", to_code, en_to_local)]
                    pivot = True
                    pivot_note = (f"No direct {from_code}->{to_code} package. "
                                  f"Installing pivot: {from_code}->en + en->{to_code}")
                    log_err(f"Found local pivot packages: {from_code}->en + en->{to_code}")

        if local_pairs:
            # Install all packages from local files (fully offline)
            installed_list = []
            for fc, tc, lpath in local_pairs:
                pkg_key = f"{fc}->{tc}"
                ok, err = install_local_package(lpath, pkg_key)
                if not ok:
                    msg, _, etype = err.rpartition("|")
                    return {
                        "success": False,
                        "error": msg,
                        "error_type": etype,
                    }
                installed_list.append(pkg_key)

            # Clear translator cache
            for key in list(_translators.keys()):
                _translators.pop(key)
            for key in list(_pivot_info.keys()):
                _pivot_info.pop(key)

            return {
                "success": True,
                "package": f"{from_code}->{to_code}",
                "installed": get_installed_packages(),
                "pivot": pivot,
                "pivot_packages": installed_list if pivot else None,
                "pivot_note": pivot_note if pivot else None,
                "offline": True,
            }

        # ---- Phase 2: Fall back to online download ----
        log_err("No local packages found, falling back to online download...")

        # Update package index
        log_err("Updating package index...")
        argostranslate.package.update_package_index()
        available = argostranslate.package.get_available_packages()

        # Determine packages to install: direct or pivot
        packages_to_install = []
        pivot = False
        pivot_note = ""

        direct = [p for p in available
                  if p.from_code == from_code and p.to_code == to_code]
        if direct:
            packages_to_install = [(from_code, to_code, direct[0])]
        else:
            # Check for pivot through English
            if from_code != "en" and to_code != "en":
                to_en = [p for p in available
                         if p.from_code == from_code and p.to_code == "en"]
                en_to = [p for p in available
                         if p.from_code == "en" and p.to_code == to_code]
                if to_en and en_to:
                    packages_to_install = [(from_code, "en", to_en[0]),
                                           ("en", to_code, en_to[0])]
                    pivot = True
                    pivot_note = (f"No direct {from_code}->{to_code} package. "
                                  f"Installing pivot: {from_code}->en + en->{to_code}")

        if not packages_to_install:
            return {
                "success": False,
                "error": (f"Package {from_code}->{to_code} not found in index. "
                          f"No direct or pivot (via English) path available."),
                "error_type": "PackageNotFound",
            }

        if pivot:
            log_err(pivot_note)

        # Install each required package
        installed_list = []
        for fc, tc, pkg in packages_to_install:
            pkg_key = f"{fc}->{tc}"
            log_err(f"Downloading package {pkg_key}...")

            try:
                path = pkg.download()
            except Exception as e:
                log_err(f"Download failed for {pkg_key}: {e}")
                return {
                    "success": False,
                    "error": f"Failed to download package {pkg_key}: {e}",
                    "error_type": "PackageDownloadFailed",
                }

            # Verify file integrity
            ok, verify_msg = verify_file_integrity(path)
            if not ok:
                try:
                    os.remove(path)
                except Exception:
                    pass
                return {
                    "success": False,
                    "error": f"Package {pkg_key} verification failed: {verify_msg}",
                    "error_type": "PackageCorrupted",
                }

            # Install
            try:
                argostranslate.package.install_from_path(path)
            except Exception as e:
                log_err(f"Install failed for {pkg_key}: {e}")
                return {
                    "success": False,
                    "error": f"Failed to install package {pkg_key}: {e}",
                    "error_type": "ModelLoadFailed",
                }

            installed_list.append(pkg_key)
            log_err(f"Package {pkg_key} installed successfully")

        # Clear translator cache
        for key in list(_translators.keys()):
            _translators.pop(key)
        for key in list(_pivot_info.keys()):
            _pivot_info.pop(key)

        return {
            "success": True,
            "package": f"{from_code}->{to_code}",
            "installed": get_installed_packages(),
            "pivot": pivot,
            "pivot_packages": installed_list if pivot else None,
            "pivot_note": pivot_note if pivot else None,
        }
    except Exception as e:
        log_err(f"install_package error: {e}\n{traceback.format_exc()}")
        error_type = "NetworkError" if "ConnectionError" in str(e) or "URLError" in str(e) else "Unknown"
        return {"success": False, "error": str(e), "error_type": error_type}


def handle_list_packages(req):
    """List installed packages."""
    return {"success": True, "packages": get_installed_packages()}


def handle_check_health(req):
    """Comprehensive health check: bridge, argostranslate, packages."""
    result = {
        "bridge_ok": True,
        "argos_installed": False,
        "argos_version": None,
        "packages": [],
        "issues": [],
    }

    # Check argostranslate
    if ensure_initialized():
        result["argos_installed"] = True
        try:
            import argostranslate
            result["argos_version"] = getattr(argostranslate, "__version__", "unknown")
        except Exception:
            result["argos_version"] = "unknown"
        result["packages"] = get_installed_packages()
    else:
        result["issues"].append("argostranslate not installed")

    result["success"] = len(result["issues"]) == 0
    return result


def handle_ping(req):
    """Health check."""
    return {"success": True, "pong": True, "initialized": _initialized}


HANDLERS = {
    "init": handle_init,
    "translate": handle_translate,
    "install_package": handle_install_package,
    "list_packages": handle_list_packages,
    "check_health": handle_check_health,
    "ping": handle_ping,
}


def main():
    log_err("Bridge started, waiting for requests...")
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        try:
            req = json.loads(line)
        except json.JSONDecodeError as e:
            send_response({"id": None, "success": False,
                           "error": f"Invalid JSON: {e}",
                           "error_type": "InvalidRequest"})
            continue

        req_id = req.get("id")
        action = req.get("action", "")

        handler = HANDLERS.get(action)
        if handler is None:
            send_response({"id": req_id, "success": False,
                           "error": f"Unknown action: {action}",
                           "error_type": "InvalidRequest"})
            continue

        try:
            result = handler(req)
            result["id"] = req_id
            send_response(result)
        except Exception as e:
            log_err(f"Handler {action} crashed: {e}\n{traceback.format_exc()}")
            send_response({
                "id": req_id,
                "success": False,
                "error": f"Internal error: {e}",
                "error_type": "InternalError",
            })

    log_err("Bridge stdin closed, exiting.")


if __name__ == "__main__":
    main()