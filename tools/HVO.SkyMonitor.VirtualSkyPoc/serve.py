#!/usr/bin/env python3
"""No-login LAN evidence server: exact file allowlist, no directory listing, byte-range video support."""
import argparse
import http.server
import json
import mimetypes
from pathlib import Path
import re
from urllib.parse import unquote, urlsplit


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    parser.add_argument("--bind", default="192.168.2.45")
    parser.add_argument("--port", type=int, default=8094)
    args = parser.parse_args()
    root = args.root.resolve(strict=True)
    allowed = {}
    for name in json.loads((root / "allowlist.json").read_text()):
        path = root / name
        resolved = path.resolve(strict=True)
        if path.is_symlink() or not resolved.is_relative_to(root) or not resolved.is_file():
            raise ValueError("Allowlist contains an unsafe path")
        allowed["/" + name] = resolved
    allowed["/"] = allowed["/index.html"]

    class Handler(http.server.BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def do_HEAD(self):
            self.serve(False)

        def do_GET(self):
            self.serve(True)

        def serve(self, body):
            path = allowed.get(unquote(urlsplit(self.path).path))
            if path is None:
                self.send_error(404)
                return
            length = path.stat().st_size
            first, last, partial = 0, length - 1, False
            range_header = self.headers.get("Range")
            if range_header:
                match = re.fullmatch(r"bytes=(\d*)-(\d*)", range_header)
                if not match or not any(match.groups()):
                    self.send_error(416)
                    return
                left, right = match.groups()
                first = int(left) if left else max(0, length - int(right))
                last = min(length - 1, int(right)) if left and right else length - 1
                if first > last or first >= length:
                    self.send_response(416)
                    self.send_header("Content-Range", f"bytes */{length}")
                    self.send_header("Content-Length", "0")
                    self.end_headers()
                    return
                partial = True
            self.send_response(206 if partial else 200)
            self.send_header("Content-Type", mimetypes.guess_type(path.name)[0] or "application/octet-stream")
            self.send_header("Content-Length", str(last - first + 1))
            self.send_header("Accept-Ranges", "bytes")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Cache-Control", "no-cache")
            self.send_header("Content-Security-Policy", "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self'; media-src 'self'; script-src 'none'; base-uri 'none'; frame-ancestors 'none'")
            if partial:
                self.send_header("Content-Range", f"bytes {first}-{last}/{length}")
            self.end_headers()
            if body:
                try:
                    with path.open("rb") as stream:
                        stream.seek(first)
                        remaining = last - first + 1
                        while remaining:
                            block = stream.read(min(256 * 1024, remaining))
                            if not block:
                                break
                            self.wfile.write(block)
                            remaining -= len(block)
                except (BrokenPipeError, ConnectionResetError):
                    pass

    server = http.server.ThreadingHTTPServer((args.bind, args.port), Handler)
    print(f"Serving {len(allowed)} exact allowlisted paths at http://{args.bind}:{args.port}/", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
