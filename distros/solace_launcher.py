#!/usr/bin/env python3
"""Solace Launcher - local web control panel for the Termux installation."""

from __future__ import annotations

import atexit
import json
import mimetypes
import os
import re
import shutil
import subprocess
import tempfile
import threading
import unicodedata
import zipfile
from datetime import datetime, timezone
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path, PurePosixPath
from urllib.parse import parse_qs, quote, unquote, urlparse

BASE_DIR = Path(__file__).resolve().parent
WEB_DIR = Path(os.environ.get("SOLACE_LAUNCHER_WEB", str(BASE_DIR / "web")))
BACKEND = Path(os.environ.get("SOLACE_BACKEND", str(BASE_DIR / "backend.sh")))
CONFIG_DIR = Path(
    os.environ.get("SOLACE_LAUNCHER_CONFIG", str(Path.home() / ".config" / "solace-launcher"))
)
PID_FILE = CONFIG_DIR / "launcher.pid"
PREFIX = Path(os.environ.get("PREFIX", "/data/data/com.termux/files/usr"))
ROOTFS = Path(
    os.environ.get(
        "SOLACE_ROOTFS",
        str(PREFIX / "var" / "lib" / "proot-distro" / "containers" / "ubuntu" / "rootfs"),
    )
)
SOLACE_ROOT = Path(os.environ.get("SOLACE_FILES_ROOT", str(ROOTFS / "root" / "Solace"))).resolve()
RESOURCE_PACK = SOLACE_ROOT / "staticdata" / "resourcepacks" / "vanilla.zip"
BIND = os.environ.get("SOLACE_LAUNCHER_BIND", "127.0.0.1")
PORT = int(os.environ.get("SOLACE_LAUNCHER_PORT", "7089"))

MAX_TEXT_BYTES = 2 * 1024 * 1024
MAX_EDIT_BYTES = 10 * 1024 * 1024
MAX_UPLOAD_BYTES = 1024 * 1024 * 1024
ANSI_RE = re.compile(r"\x1b(?:\[[0-?]*[ -/]*[@-~]|[@-_])")
OSC_RE = re.compile(r"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)?", re.DOTALL)
EMOJI_RE = re.compile(
    "[\U0001f000-\U0001faff\u2139\u2600-\u27bf\u2b00-\u2bff\ufe0f\u200d]"
)
MUTATION_LOCK = threading.Lock()

OPERATION_TIMEOUTS = {
    "start": 240,
    "restart": 360,
    "stop": 240,
    "update": 900,
    "branch": 900,
    "eula": 30,
    "public-ip": 30,
    "reset": 300,
    "logs": 120,
    "processes": 30,
}


class BackendError(RuntimeError):
    def __init__(self, message: str, returncode: int = 1, output: str = "") -> None:
        super().__init__(message)
        self.returncode = returncode
        self.output = output


def clean_output(value: str) -> str:
    value = OSC_RE.sub("", value)
    value = ANSI_RE.sub("", value)
    value = EMOJI_RE.sub("", value)
    value = value.replace("\r\n", "\n").replace("\r", "\n")
    return "".join(
        ch
        for ch in value
        if ch in "\n\t" or (ord(ch) >= 32 and unicodedata.category(ch) != "So")
    )


def run_backend(args: list[str], timeout: int = 60) -> str:
    env = os.environ.copy()
    env.setdefault("PREFIX", str(PREFIX))
    try:
        result = subprocess.run(
            [str(BACKEND), *args],
            capture_output=True,
            text=True,
            timeout=timeout,
            env=env,
        )
    except subprocess.TimeoutExpired as exc:
        output = (exc.stdout or "") + (exc.stderr or "")
        if isinstance(output, bytes):
            output = output.decode("utf-8", "replace")
        raise BackendError("The operation timed out.", output=clean_output(output)) from exc
    except OSError as exc:
        raise BackendError(f"Could not run the Solace backend: {exc}") from exc

    output = "\n".join(
        part.strip() for part in (result.stdout, result.stderr) if part and part.strip()
    )
    output = clean_output(output)
    if result.returncode != 0:
        raise BackendError(output or "The operation failed.", result.returncode, output)
    return output


def backend_status() -> dict:
    output = run_backend(["status", "--json"], timeout=30)
    for line in reversed(output.splitlines()):
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            status = json.loads(line)
        except json.JSONDecodeError:
            continue
        pack_size = 0
        pack_modified = ""
        try:
            stat = RESOURCE_PACK.stat()
            pack_size = stat.st_size
            pack_modified = datetime.fromtimestamp(stat.st_mtime, timezone.utc).isoformat()
        except OSError:
            pass
        status["resourcePack"] = RESOURCE_PACK.is_file()
        status["resourcePackSize"] = pack_size
        status["resourcePackModified"] = pack_modified
        status["filesRoot"] = "/root/Solace"
        return status
    raise BackendError(output or "The backend returned an invalid status.")


def normalise_relative(value: str) -> str:
    raw = unquote(value or "").replace("\\", "/").strip()
    if raw in {"", ".", "/"}:
        return ""
    parts: list[str] = []
    for part in raw.split("/"):
        if part in {"", "."}:
            continue
        if part == "..":
            raise ValueError("Parent directory traversal is not allowed.")
        parts.append(part)
    return "/".join(parts)


def resolve_file(value: str) -> Path:
    relative = normalise_relative(value)
    candidate = (SOLACE_ROOT / relative).resolve()
    try:
        candidate.relative_to(SOLACE_ROOT)
    except ValueError as exc:
        raise ValueError("The path is outside the Solace folder.") from exc
    return candidate


def relative_file(path: Path) -> str:
    try:
        relative = path.relative_to(SOLACE_ROOT).as_posix()
    except ValueError as exc:
        raise ValueError("The path is outside the Solace folder.") from exc
    return "" if relative == "." else relative


def parent_relative(relative: str) -> str | None:
    if not relative:
        return None
    parent = PurePosixPath(relative).parent
    return "" if str(parent) == "." else parent.as_posix()


def file_entry(path: Path) -> dict:
    stat = path.lstat()
    is_link = path.is_symlink()
    is_dir = path.is_dir() and not is_link
    suffix = path.suffix.lower()
    editable = False
    if path.is_file() and not is_link and stat.st_size <= MAX_TEXT_BYTES:
        editable = suffix not in {
            ".png",
            ".jpg",
            ".jpeg",
            ".gif",
            ".webp",
            ".ico",
            ".zip",
            ".jar",
            ".dll",
            ".exe",
            ".so",
            ".gz",
            ".tar",
            ".7z",
            ".pdf",
            ".woff",
            ".woff2",
            ".ttf",
            ".otf",
        }
    return {
        "name": path.name,
        "type": "dir" if is_dir else ("link" if is_link else "file"),
        "size": stat.st_size,
        "modified": datetime.fromtimestamp(stat.st_mtime, timezone.utc).isoformat(),
        "editable": editable,
    }


def list_files(relative: str, show_hidden: bool) -> dict:
    target = resolve_file(relative)
    if not target.is_dir():
        raise FileNotFoundError("The folder does not exist.")
    current = relative_file(target)
    entries = []
    for child in sorted(target.iterdir(), key=lambda item: (not item.is_dir(), item.name.lower())):
        if not show_hidden and child.name.startswith("."):
            continue
        try:
            entries.append(file_entry(child))
        except OSError:
            continue
    return {
        "path": current,
        "parent": parent_relative(current),
        "entries": entries,
    }


def validate_zip(path: Path) -> None:
    try:
        valid = zipfile.is_zipfile(path)
    except OSError as exc:
        raise ValueError("The resource pack could not be read.") from exc
    if not valid:
        raise ValueError("Upload a valid vanilla.zip archive.")


class LauncherHandler(BaseHTTPRequestHandler):
    server_version = "SolaceLauncher/2.0"

    def log_message(self, fmt: str, *args) -> None:
        print(f"[launcher] {self.address_string()} {fmt % args}", flush=True)

    def _common_headers(self) -> None:
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header(
            "Content-Security-Policy",
            "default-src 'self'; img-src 'self' data:; style-src 'self'; "
            "script-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'self'",
        )

    def _send_json(self, data: dict, status: int = 200) -> None:
        body = json.dumps(data, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self._common_headers()
        self.end_headers()
        self.wfile.write(body)

    def _send_text(
        self, body: str, status: int = 200, content_type: str = "text/plain; charset=utf-8"
    ) -> None:
        data = body.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self._common_headers()
        self.end_headers()
        self.wfile.write(data)

    def _read_json(self) -> dict:
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError as exc:
            raise ValueError("A valid Content-Length header is required.") from exc
        if length > 64 * 1024:
            raise ValueError("The request body is too large.")
        if length <= 0:
            return {}
        raw = self.rfile.read(length)
        try:
            data = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise ValueError("The request body must be JSON.") from exc
        return data if isinstance(data, dict) else {}

    def _check_origin(self) -> None:
        origin = self.headers.get("Origin")
        if not origin:
            return
        host = self.headers.get("Host", "")
        if urlparse(origin).netloc != host:
            raise PermissionError("Cross-origin requests are not allowed.")

    def _safe_static_path(self, path: str) -> Path | None:
        relative = unquote(path).lstrip("/")
        if not relative:
            relative = "index.html"
        candidate = (WEB_DIR / relative).resolve()
        try:
            candidate.relative_to(WEB_DIR.resolve())
        except ValueError:
            return None
        return candidate

    def _serve_static(self, path: str) -> None:
        if path == "/favicon.ico":
            path = "/assets/favicon.png"
        target = self._safe_static_path(path)
        if target is None or not target.is_file():
            self._send_text("Not found", HTTPStatus.NOT_FOUND)
            return
        data = target.read_bytes()
        content_type = mimetypes.guess_type(str(target))[0] or "application/octet-stream"
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        if target.suffix.lower() in {".png", ".jpg", ".jpeg", ".webp", ".svg", ".ico"}:
            self.send_header("Cache-Control", "public, max-age=3600")
        else:
            self.send_header("Cache-Control", "no-store")
        self._common_headers()
        self.end_headers()
        self.wfile.write(data)

    def _send_file(self, path: Path, download: bool) -> None:
        stat = path.stat()
        content_type = mimetypes.guess_type(str(path))[0] or "application/octet-stream"
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(stat.st_size))
        self.send_header("Cache-Control", "no-store")
        if download:
            encoded = quote(path.name)
            self.send_header(
                "Content-Disposition", f"attachment; filename*=UTF-8''{encoded}"
            )
        self._common_headers()
        self.end_headers()
        with path.open("rb") as source:
            shutil.copyfileobj(source, self.wfile)

    def _receive_to_file(self, target: Path, max_bytes: int, validate=None) -> int:
        try:
            length = int(self.headers.get("Content-Length", "-1"))
        except ValueError as exc:
            raise ValueError("A valid Content-Length header is required.") from exc
        if length < 0:
            raise ValueError("A Content-Length header is required for uploads.")
        if length > max_bytes:
            raise ValueError("The upload is too large.")
        if length == 0:
            raise ValueError("The upload is empty.")

        parent = target.parent
        if not parent.is_dir():
            raise FileNotFoundError("The destination folder does not exist.")
        descriptor, temporary = tempfile.mkstemp(prefix=".solace-upload-", dir=parent)
        temporary_path = Path(temporary)
        try:
            remaining = length
            with os.fdopen(descriptor, "wb") as output:
                while remaining > 0:
                    chunk = self.rfile.read(min(remaining, 1024 * 1024))
                    if not chunk:
                        raise ValueError("The upload ended before all bytes were received.")
                    output.write(chunk)
                    remaining -= len(chunk)
                output.flush()
                os.fsync(output.fileno())
            if validate is not None:
                validate(temporary_path)
            os.replace(temporary_path, target)
            os.chmod(target, 0o644)
            return length
        finally:
            if temporary_path.exists():
                temporary_path.unlink()

    def _run_action(self, name: str, args: list[str]) -> None:
        with MUTATION_LOCK:
            output = run_backend(args, timeout=OPERATION_TIMEOUTS.get(name, 120))
        self._send_json({"ok": True, "output": clean_output(output)})

    def do_GET(self) -> None:  # noqa: N802
        parsed = urlparse(self.path)
        path = parsed.path
        query = parse_qs(parsed.query)
        try:
            if path == "/api/health":
                self._send_json({"ok": True, "name": "Solace Launcher", "port": PORT})
                return
            if path == "/api/status":
                try:
                    self._send_json({"ok": True, "status": backend_status()})
                except BackendError as exc:
                    self._send_json(
                        {"ok": False, "error": str(exc), "status": {"state": "unknown"}},
                        HTTPStatus.SERVICE_UNAVAILABLE,
                    )
                return
            if path == "/api/logs":
                service = (query.get("source", ["startup"])[0] or "startup").strip()
                if not re.fullmatch(r"[A-Za-z0-9_-]{1,40}", service):
                    self._send_json(
                        {"ok": False, "error": "Invalid log source."}, HTTPStatus.BAD_REQUEST
                    )
                    return
                try:
                    lines = int(query.get("lines", ["300"])[0])
                except ValueError:
                    lines = 300
                lines = max(20, min(lines, 1000))
                output = run_backend(
                    ["logs", service, str(lines)], timeout=OPERATION_TIMEOUTS["logs"]
                )
                self._send_json(
                    {"ok": True, "source": service, "lines": clean_output(output)}
                )
                return
            if path == "/api/processes":
                output = run_backend(
                    ["processes"], timeout=OPERATION_TIMEOUTS["processes"]
                )
                self._send_json({"ok": True, "processes": clean_output(output)})
                return
            if path == "/api/files":
                relative = query.get("path", [""])[0]
                hidden = query.get("hidden", ["0"])[0] in {"1", "true", "yes"}
                self._send_json({"ok": True, **list_files(relative, hidden)})
                return
            if path == "/api/files/read":
                relative = query.get("path", [""])[0]
                target = resolve_file(relative)
                if not target.is_file():
                    raise FileNotFoundError("The file does not exist.")
                if target.stat().st_size > MAX_TEXT_BYTES:
                    self._send_json(
                        {"ok": False, "error": "This file is too large to edit."},
                        HTTPStatus.REQUEST_ENTITY_TOO_LARGE,
                    )
                    return
                raw = target.read_bytes()
                if b"\x00" in raw:
                    self._send_json(
                        {"ok": False, "error": "This looks like a binary file."},
                        HTTPStatus.UNSUPPORTED_MEDIA_TYPE,
                    )
                    return
                try:
                    text = raw.decode("utf-8")
                except UnicodeDecodeError:
                    self._send_json(
                        {"ok": False, "error": "This file is not UTF-8 text."},
                        HTTPStatus.UNSUPPORTED_MEDIA_TYPE,
                    )
                    return
                self._send_json(
                    {"ok": True, "path": relative_file(target), "text": text}
                )
                return
            if path == "/api/files/download":
                relative = query.get("path", [""])[0]
                target = resolve_file(relative)
                if not target.is_file():
                    raise FileNotFoundError("The file does not exist.")
                self._send_file(target, download=True)
                return
            self._serve_static(path)
        except ValueError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.BAD_REQUEST)
        except PermissionError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.FORBIDDEN)
        except FileNotFoundError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.NOT_FOUND)
        except BackendError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.BAD_GATEWAY)
        except OSError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.INTERNAL_SERVER_ERROR)

    def do_POST(self) -> None:  # noqa: N802
        path = urlparse(self.path).path
        try:
            self._check_origin()
            body = self._read_json()
            if path == "/api/start":
                self._run_action("start", ["start"])
            elif path == "/api/restart":
                self._run_action("restart", ["restart"])
            elif path == "/api/stop":
                self._run_action("stop", ["stop"])
            elif path == "/api/update":
                self._run_action("update", ["update"])
            elif path == "/api/eula":
                action = str(body.get("action", "accept")).lower()
                if action in {"delete", "revoke"}:
                    self._run_action("eula", ["eula", "delete"])
                else:
                    self._run_action("eula", ["eula", "accept"])
            elif path == "/api/public-ip":
                value = str(body.get("ip", "")).strip()
                if not re.fullmatch(r"[A-Za-z0-9.:-]{1,253}", value):
                    self._send_json(
                        {"ok": False, "error": "Enter a valid IP address or hostname."},
                        HTTPStatus.BAD_REQUEST,
                    )
                    return
                self._run_action("public-ip", ["set-public-ip", value])
            elif path == "/api/branch":
                value = str(body.get("branch", "")).strip()
                if (
                    not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._/-]{0,79}", value)
                    or ".." in value
                    or value.endswith("/")
                ):
                    self._send_json(
                        {"ok": False, "error": "Enter a valid branch name."},
                        HTTPStatus.BAD_REQUEST,
                    )
                    return
                self._run_action("branch", ["switch-branch", value])
            elif path == "/api/reset-databases":
                if body.get("confirm") != "RESET":
                    self._send_json(
                        {"ok": False, "error": "Type RESET to confirm."},
                        HTTPStatus.BAD_REQUEST,
                    )
                    return
                self._run_action("reset", ["reset-databases", "--yes"])
            elif path == "/api/files/mkdir":
                relative = str(body.get("path", ""))
                target = resolve_file(relative)
                if target == SOLACE_ROOT:
                    raise ValueError("Choose a folder name.")
                if target.exists():
                    raise ValueError("A file or folder with that name already exists.")
                if not target.parent.is_dir():
                    raise FileNotFoundError("The parent folder does not exist.")
                target.mkdir()
                self._send_json({"ok": True, "path": relative_file(target)})
            else:
                self._send_json(
                    {"ok": False, "error": "Unknown endpoint."}, HTTPStatus.NOT_FOUND
                )
        except ValueError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.BAD_REQUEST)
        except PermissionError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.FORBIDDEN)
        except FileNotFoundError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.NOT_FOUND)
        except BackendError as exc:
            status = HTTPStatus.CONFLICT if exc.returncode == 42 else HTTPStatus.BAD_GATEWAY
            payload = {"ok": False, "error": str(exc), "output": exc.output}
            if exc.returncode == 42:
                payload["eulaRequired"] = True
            self._send_json(payload, status)
        except OSError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.INTERNAL_SERVER_ERROR)

    def do_PUT(self) -> None:  # noqa: N802
        parsed = urlparse(self.path)
        path = parsed.path
        query = parse_qs(parsed.query)
        try:
            self._check_origin()
            if path == "/api/resourcepack":
                RESOURCE_PACK.parent.mkdir(parents=True, exist_ok=True)
                size = self._receive_to_file(
                    RESOURCE_PACK, MAX_UPLOAD_BYTES, validate=validate_zip
                )
                self._send_json(
                    {"ok": True, "size": size, "path": "staticdata/resourcepacks/vanilla.zip"}
                )
                return
            if path == "/api/files/write":
                relative = query.get("path", [""])[0]
                target = resolve_file(relative)
                if target == SOLACE_ROOT:
                    raise ValueError("Choose a file name.")
                if target.is_dir():
                    raise IsADirectoryError("The destination is a folder.")
                if not target.parent.is_dir():
                    raise FileNotFoundError("The destination folder does not exist.")
                content_type = self.headers.get("Content-Type", "")
                maximum = MAX_EDIT_BYTES if "text/" in content_type else MAX_UPLOAD_BYTES
                size = self._receive_to_file(target, maximum)
                self._send_json({"ok": True, "size": size, "path": relative_file(target)})
                return
            self._send_json({"ok": False, "error": "Unknown endpoint."}, HTTPStatus.NOT_FOUND)
        except ValueError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.BAD_REQUEST)
        except PermissionError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.FORBIDDEN)
        except FileNotFoundError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.NOT_FOUND)
        except IsADirectoryError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.BAD_REQUEST)
        except OSError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.INTERNAL_SERVER_ERROR)

    def do_DELETE(self) -> None:  # noqa: N802
        parsed = urlparse(self.path)
        if parsed.path != "/api/files":
            self._send_json({"ok": False, "error": "Unknown endpoint."}, HTTPStatus.NOT_FOUND)
            return
        try:
            self._check_origin()
            relative = parse_qs(parsed.query).get("path", [""])[0]
            target = resolve_file(relative)
            if target == SOLACE_ROOT:
                raise ValueError("The Solace folder cannot be deleted here.")
            if not target.exists():
                raise FileNotFoundError("The file or folder does not exist.")
            if target.is_dir() and not target.is_symlink():
                shutil.rmtree(target)
            else:
                target.unlink()
            self._send_json({"ok": True, "path": relative})
        except ValueError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.BAD_REQUEST)
        except PermissionError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.FORBIDDEN)
        except FileNotFoundError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.NOT_FOUND)
        except OSError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.INTERNAL_SERVER_ERROR)


class LauncherServer(ThreadingHTTPServer):
    daemon_threads = True


def main() -> None:
    CONFIG_DIR.mkdir(parents=True, exist_ok=True)
    if not BACKEND.is_file():
        raise SystemExit(f"Solace backend not found: {BACKEND}")
    if not WEB_DIR.is_dir():
        raise SystemExit(f"Solace Launcher web files not found: {WEB_DIR}")

    atexit.register(lambda: PID_FILE.unlink(missing_ok=True))
    PID_FILE.write_text(f"{os.getpid()}\n", encoding="utf-8")
    server = LauncherServer((BIND, PORT), LauncherHandler)
    print(f"Solace Launcher listening on http://{BIND}:{PORT}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
        PID_FILE.unlink(missing_ok=True)


if __name__ == "__main__":
    main()
