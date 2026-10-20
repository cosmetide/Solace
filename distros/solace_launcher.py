#!/usr/bin/env python3
"""Solace Launcher - a small local web UI for the Termux installation."""

from __future__ import annotations

import atexit
import json
import mimetypes
import os
import re
import subprocess
import threading
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, unquote, urlparse

BASE_DIR = Path(__file__).resolve().parent
WEB_DIR = Path(os.environ.get("SOLACE_LAUNCHER_WEB", str(BASE_DIR / "web")))
BACKEND = Path(os.environ.get("SOLACE_BACKEND", str(BASE_DIR / "backend.sh")))
CONFIG_DIR = Path(
    os.environ.get("SOLACE_LAUNCHER_CONFIG", str(Path.home() / ".config" / "solace-launcher"))
)
PID_FILE = CONFIG_DIR / "launcher.pid"
BIND = os.environ.get("SOLACE_LAUNCHER_BIND", "127.0.0.1")
PORT = int(os.environ.get("SOLACE_LAUNCHER_PORT", "7089"))

MUTATION_LOCK = threading.Lock()
OPERATION_TIMEOUTS = {
    "start": 180,
    "stop": 180,
    "update": 600,
    "eula": 30,
    "public-ip": 30,
    "reset": 180,
    "admin-password": 150,
    "logs": 90,
}


class BackendError(RuntimeError):
    def __init__(self, message: str, returncode: int = 1, output: str = "") -> None:
        super().__init__(message)
        self.returncode = returncode
        self.output = output


def run_backend(args: list[str], timeout: int = 60) -> str:
    env = os.environ.copy()
    env.setdefault("PREFIX", "/data/data/com.termux/files/usr")
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
        raise BackendError("The operation timed out.", output=output) from exc
    except OSError as exc:
        raise BackendError(f"Could not run the Solace backend: {exc}") from exc

    output = "\n".join(
        part.strip() for part in (result.stdout, result.stderr) if part and part.strip()
    )
    if result.returncode != 0:
        raise BackendError(output or "The operation failed.", result.returncode, output)
    return output


def backend_status() -> dict:
    output = run_backend(["status", "--json"], timeout=30)
    for line in reversed(output.splitlines()):
        line = line.strip()
        if line.startswith("{"):
            try:
                return json.loads(line)
            except json.JSONDecodeError:
                continue
    raise BackendError(output or "The backend returned an invalid status.")



class LauncherHandler(BaseHTTPRequestHandler):
    server_version = "SolaceLauncher/1.0"

    def log_message(self, fmt: str, *args) -> None:
        print(f"[launcher] {self.address_string()} {fmt % args}", flush=True)

    def _send_json(self, data: dict, status: int = 200) -> None:
        body = json.dumps(data, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def _send_text(self, body: str, status: int = 200, content_type: str = "text/plain; charset=utf-8") -> None:
        data = body.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def _read_json(self) -> dict:
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            length = 0
        if length > 64 * 1024:
            raise ValueError("Request body is too large.")
        if length <= 0:
            return {}
        raw = self.rfile.read(length)
        try:
            data = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise ValueError("Request body must be JSON.") from exc
        return data if isinstance(data, dict) else {}

    def _safe_path(self, path: str) -> Path | None:
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
            path = "/assets/branding.png"
        target = self._safe_path(path)
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
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self) -> None:  # noqa: N802
        parsed = urlparse(self.path)
        path = parsed.path
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
            query = parse_qs(parsed.query)
            service = (query.get("source", ["startup"])[0] or "startup").strip()
            if not re.fullmatch(r"[A-Za-z0-9_-]{1,40}", service):
                self._send_json({"ok": False, "error": "Invalid log source."}, HTTPStatus.BAD_REQUEST)
                return
            try:
                lines = int(query.get("lines", ["200"])[0])
            except ValueError:
                lines = 200
            lines = max(20, min(lines, 1000))
            try:
                output = run_backend(["logs", service, str(lines)], timeout=OPERATION_TIMEOUTS["logs"])
                self._send_json({"ok": True, "source": service, "lines": output})
            except BackendError as exc:
                self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.BAD_GATEWAY)
            return
        if path == "/api/admin-password":
            try:
                output = run_backend(["admin-password"], timeout=OPERATION_TIMEOUTS["admin-password"])
                self._send_json({"ok": True, "password": output.strip()})
            except BackendError as exc:
                self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.BAD_GATEWAY)
            return
        self._serve_static(path)

    def do_POST(self) -> None:  # noqa: N802
        path = urlparse(self.path).path
        try:
            body = self._read_json()
        except ValueError as exc:
            self._send_json({"ok": False, "error": str(exc)}, HTTPStatus.BAD_REQUEST)
            return

        try:
            if path == "/api/start":
                self._run_mutation("start", ["start"])
            elif path == "/api/stop":
                self._run_mutation("stop", ["stop"])
            elif path == "/api/update":
                self._run_mutation("update", ["update"])
            elif path == "/api/eula":
                action = str(body.get("action", "accept")).lower()
                if action in {"delete", "revoke"}:
                    self._run_mutation("eula", ["eula", "delete"])
                else:
                    self._run_mutation("eula", ["eula", "accept"])
            elif path == "/api/public-ip":
                value = str(body.get("ip", "")).strip()
                if not re.fullmatch(r"[A-Za-z0-9.:-]{1,253}", value):
                    self._send_json({"ok": False, "error": "Enter a valid IP address or hostname."}, HTTPStatus.BAD_REQUEST)
                    return
                self._run_mutation("public-ip", ["set-public-ip", value])
            elif path == "/api/reset-databases":
                if body.get("confirm") != "RESET":
                    self._send_json({"ok": False, "error": "Confirmation is required."}, HTTPStatus.BAD_REQUEST)
                    return
                self._run_mutation("reset", ["reset-databases", "--yes"])
            else:
                self._send_json({"ok": False, "error": "Unknown endpoint."}, HTTPStatus.NOT_FOUND)
        except BackendError as exc:
            status = HTTPStatus.CONFLICT if exc.returncode == 42 else HTTPStatus.BAD_GATEWAY
            payload = {"ok": False, "error": str(exc), "output": exc.output}
            if exc.returncode == 42:
                payload["eulaRequired"] = True
            self._send_json(payload, status)

    def _run_mutation(self, name: str, args: list[str]) -> None:
        with MUTATION_LOCK:
            output = run_backend(args, timeout=OPERATION_TIMEOUTS.get(name, 120))
        self._send_json({"ok": True, "output": output})


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
