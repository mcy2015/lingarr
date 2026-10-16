#!/usr/bin/env python3
"""Fake subtitle-source server for Lingarr E2E rounds (PRD AC25, tech design M2).

One process, one port, seven sources under distinct path prefixes — the
wire shapes are byte-compatible with the fixtures in
Lingarr.Server.Tests/Fixtures/Subtitles/, so unit tests and E2E rounds
exercise the same parser contracts.

Usage
-----
    python3 scripts/dev/fake-subtitle-sources.py [--host 127.0.0.1] [--port 8799]

Point a source at the fake by setting its base-url override (settings card
or settings store) to the printed value, e.g.:

    subtitle_opensubtitles_base_url = http://127.0.0.1:8799/opensubtitles/api/v1
    subtitle_subsource_base_url     = http://127.0.0.1:8799/subsource/api
    subtitle_subdl_base_url         = http://127.0.0.1:8799/subdl
    subtitle_gestdown_base_url      = http://127.0.0.1:8799/gestdown
    subtitle_podnapisi_base_url     = http://127.0.0.1:8799/podnapisi
    subtitle_assrt_base_url         = http://127.0.0.1:8799/assrt/v1
    subtitle_shooter_base_url       = http://127.0.0.1:8799/shooter

Credentials: any non-empty value passes — opensubtitles/assrt accept
"test-key"/"test-token"; empty credentials still fail before the request is
sent (provider-side check), which is part of the contract under test.

Fault injection (any endpoint, query parameter controlled)
----------------------------------------------------------
    ?_fault=429            -> HTTP 429 + Retry-After: 3600 (or _retry_after=N)
    ?_fault=500            -> HTTP 500 (cooldown bucket check)
    ?_fault=empty          -> HTTP 200 with the source's empty-result body
    ?_fault=timeout        -> sleep _delay seconds (default 30, > the 10s
                              per-source timeout) then answer 200

Example: curl 'http://127.0.0.1:8799/gestdown/subtitles/get/x/1/2/en?_fault=429'
"""

from __future__ import annotations

import argparse
import io
import json
import re
import sys
import time
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

SRT_BODY = "\r\n".join([
    "1",
    "00:00:01,000 --> 00:00:03,000",
    "Fake subtitle from the Lingarr fake-source server.",
    "",
    "2",
    "00:00:04,000 --> 00:00:06,000",
    "Second cue.",
    "",
])

OS_SEARCH_RESPONSE = {
    "total_pages": 1,
    "data": [
        {
            "id": "297233-2195383",
            "type": "subtitle",
            "attributes": {
                "subtitle_id": "297233",
                "language": "eng",
                "forced": False,
                "hearing_impaired": False,
                "hd": True,
                "fps": 23.976,
                "release_notes": "Movie.2019.WEBDL-1080p",
                "uploader": {"name": "fixture-uploader", "rank": "gold"},
                "files": [
                    {
                        "file_id": 100613334,
                        "cd_number": 1,
                        "file_name": "Movie.2019.WEBDL-1080p.srt",
                    }
                ],
            },
        }
    ],
}

SUBSOURCE_MOVIE_RESPONSE = {
    "found": True,
    "subs": [
        {
            "releaseName": "Movie.2019.WEBDL-1080p",
            "linkName": "movie-2019-webdl-1080p",
            "lang": "ENG",
            "author": "fixture-author",
            "hearingImpaired": False,
        }
    ],
}

SUBSOURCE_SEASON_RESPONSE = {
    "found": True,
    "subs": [
        {
            "releaseName": "Show.S01E02.WEBDL-1080p",
            "linkName": "show-s01e02-webdl-1080p",
            "lang": "ENG",
            "author": "fixture-author",
            "hearingImpaired": False,
        }
    ],
}

SUBDL_RESPONSE = {
    "status": True,
    "error": None,
    "results": [
        {
            "subtitle": "/subtitle/movie-2019-ZH.zip",
            "release_name": "Movie.2019.WEBDL-1080p",
            "language": "ZH",
            "author": "fixture-author",
            "forced": False,
        }
    ],
}

GESTDOWN_SHOWS = [
    {"uuid": "d2b7db2b-4d3b-4a72-ba0f-23f2c5a1a123", "name": "Show"},
    {"uuid": "e3c8ec3c-5e4c-5b83-cb1f-34a3d6b2b234", "name": "Other Show"},
]

GESTDOWN_SUBTITLES = {
    "episode": {"season": 1, "episode": 2, "title": "Episode Two"},
    "subtitles": [
        {"uuid": "sub-uuid-1", "version": "720p.WEB-DL", "language": "en"},
        {"uuid": "sub-uuid-2", "version": "1080p.WEB-DL", "language": "en"},
    ],
}

PODNAPISI_XML = """<?xml version="1.0" encoding="UTF-8"?>
<subtitles>
  <subtitle id="4123456"
            title="Movie 2019"
            release="Movie.2019.WEBDL-1080p"
            link="/subtitles/download/4123456"
            language="en"
            format="zip"
            hearing-impaired="0"
            uploader="fixture-uploader"/>
</subtitles>
"""

ASSRT_SEARCH_RESPONSE = {
    "status": 0,
    "sub": {
        "subs": [
            {"id": 671234, "title": "Movie 2019", "native_name": "电影2019"}
        ]
    },
}

ASSRT_DETAIL_TEMPLATE = {
    "status": 0,
    "sub": {
        "subs": [
            {
                "id": 671234,
                "title": "Movie 2019",
                "native_name": "电影2019",
                "filelist": [
                    # url placeholder rewritten per request (see _self_url)
                    {"f": "Movie.2019.WEBDL-1080p.chs.ass", "url": "http://SELF/assrt/sub/file"}
                ],
            }
        ]
    },
}

SHOOTER_TEMPLATE = [
    {
        "Desc": "Movie.2019.WEBDL-1080p.chs",
        "Delay": 0,
        "Files": [
            # Link placeholder rewritten per request (see _self_url)
            {"Ext": "srt", "Link": "http://SELF/shooter/file.srt"}
        ],
    }
]

SRT_BYTES = SRT_BODY.encode("utf-8")


def zip_bytes(entries: dict[str, bytes]) -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_STORED) as archive:
        for name, content in entries.items():
            archive.writestr(name, content)
    return buffer.getvalue()


class FakeSourcesHandler(BaseHTTPRequestHandler):
    server_version = "LingarrFakeSources/1.0"

    # ------------------------------------------------------------- plumbing

    def log_message(self, fmt, *args):
        sys.stderr.write("[fake-sources] %s - %s\n" % (self.address_string(), fmt % args))

    def _fault(self, query: dict[str, list[str]]) -> str | None:
        return query.get("_fault", [None])[0]

    def _apply_fault(self, query) -> bool:
        """Returns True when the request was answered by a fault injection."""
        fault = self._fault(query)
        if fault == "429":
            retry_after = query.get("_retry_after", ["3600"])[0]
            self.send_response(429)
            self.send_header("Retry-After", retry_after)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b'{"message": "fake rate limit"}')
            return True
        if fault == "500":
            self.send_response(500)
            self.send_header("Content-Type", "text/plain")
            self.end_headers()
            self.wfile.write(b"fake server error")
            return True
        if fault == "timeout":
            time.sleep(float(query.get("_delay", ["30"])[0]))
            return False  # answer the normal payload late
        return False

    def _empty(self, query) -> bool:
        return self._fault(query) == "empty"

    def _send_json(self, payload, status: int = 200):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _self_base(self) -> str:
        return "http://%s" % (self.headers.get("Host") or "127.0.0.1")

    def _send_json_selfrewritten(self, template, status: int = 200):
        """Sends a template after replacing http://SELF with this server's origin.

        Source responses that embed absolute download URLs (assrt filelist,
        shooter file links) must point back at the fake server for the E2E
        download step to resolve.
        """
        body = json.dumps(template).replace("http://SELF", self._self_base()).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _send_bytes(self, body: bytes, content_type: str, filename: str | None = None):
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        if filename:
            self.send_header("Content-Disposition", 'attachment; filename="%s"' % filename)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    # ------------------------------------------------------------- methods

    def do_GET(self):
        self._route("GET")

    def do_POST(self):
        # Drain the request body so keep-alive connections stay usable.
        length = int(self.headers.get("Content-Length") or 0)
        if length:
            self.rfile.read(length)
        self._route("POST")

    def _route(self, method: str):
        parsed = urlparse(self.path)
        path = parsed.path
        query = parse_qs(parsed.query)

        if self._apply_fault(query):
            return

        try:
            if path.startswith("/opensubtitles/"):
                self._route_opensubtitles(path, query)
            elif path.startswith("/subsource/"):
                self._route_subsource(path, query, method)
            elif path.startswith("/subdl/"):
                self._route_subdl(path, query)
            elif path.startswith("/gestdown/"):
                self._route_gestdown(path, query)
            elif path.startswith("/podnapisi/"):
                self._route_podnapisi(path, query)
            elif path.startswith("/assrt/"):
                self._route_assrt(path, query)
            elif path.startswith("/shooter/"):
                self._route_shooter(path, query)
            else:
                self.send_response(404)
                self.send_header("Content-Type", "text/plain")
                self.end_headers()
                self.wfile.write(b"unknown fake source prefix")
        except BrokenPipeError:
            pass

    # ------------------------------------------------------------- sources

    def _route_opensubtitles(self, path, query):
        if path.endswith("/subtitles"):
            if self._empty(query):
                self._send_json({"total_pages": 1, "data": []})
                return
            self._send_json(OS_SEARCH_RESPONSE)
        elif path.endswith("/download"):
            # POST /download answers with a link pointing at this server.
            self._send_json({
                "link": "http://%s/opensubtitles/download/file" % self.headers.get("Host", "127.0.0.1"),
                "file_name": "Movie.2019.WEBDL-1080p.srt",
                "remaining_downloads": 39,
            })
        elif "/download/file" in path:
            self._send_bytes(SRT_BYTES, "application/octet-stream", "Movie.2019.WEBDL-1080p.srt")
        else:
            self.send_response(404)
            self.end_headers()

    def _route_subsource(self, path, query, method):
        if path.endswith("/searchMovie"):
            if self._empty(query):
                self._send_json({"found": False, "subs": []})
            else:
                self._send_json(SUBSOURCE_MOVIE_RESPONSE)
        elif path.endswith("/searchSeason"):
            if self._empty(query):
                self._send_json({"found": False, "subs": []})
            else:
                self._send_json(SUBSOURCE_SEASON_RESPONSE)
        elif path.endswith("/getSub"):
            self._send_json({"sub": [SRT_BODY]})
        else:
            self.send_response(404)
            self.end_headers()

    def _route_subdl(self, path, query):
        if path.endswith("/auto"):
            if self._empty(query):
                self._send_json({"status": False, "error": None, "results": []})
            else:
                # Site-relative download path: stays on this server.
                self._send_json(SUBDL_RESPONSE)
        elif "/subtitle/" in path:
            payload = zip_bytes({"Movie.2019.WEBDL-1080p.zh.srt": SRT_BYTES})
            self._send_bytes(payload, "application/zip", "movie-2019-ZH.zip")
        else:
            self.send_response(404)
            self.end_headers()

    def _route_gestdown(self, path, query):
        if "/shows/search" in path:
            if self._empty(query):
                self._send_json([])
            else:
                self._send_json(GESTDOWN_SHOWS)
        elif re.search(r"/subtitles/get/[^/]+/\d+/\d+/[a-z-]+", path):
            if self._empty(query):
                self._send_json({"episode": {}, "subtitles": []})
            else:
                self._send_json(GESTDOWN_SUBTITLES)
        elif "/subtitles/download/" in path:
            self._send_bytes(SRT_BYTES, "application/x-subrip", "subtitle.srt")
        else:
            self.send_response(404)
            self.end_headers()

    def _route_podnapisi(self, path, query):
        if "/subtitles/search" in path:
            if self._empty(query):
                self._send_bytes(b"<?xml version='1.0' encoding='UTF-8'?>\n<subtitles>\n</subtitles>\n", "application/xml")
            else:
                self._send_bytes(PODNAPISI_XML.encode("utf-8"), "application/xml")
        elif "/subtitles/download/" in path:
            payload = zip_bytes({"4123456.srt": SRT_BYTES})
            self._send_bytes(payload, "application/zip", "4123456.zip")
        else:
            self.send_response(404)
            self.end_headers()

    def _route_assrt(self, path, query):
        token = (query.get("token") or [""])[0]
        if not token:
            self._send_json({"status": 1, "message": "token required"}, status=200)
            return
        if "/sub/search" in path:
            if self._empty(query):
                self._send_json({"status": 0, "sub": {"subs": []}})
            else:
                self._send_json(ASSRT_SEARCH_RESPONSE)
        elif "/sub/detail" in path:
            self._send_json_selfrewritten(ASSRT_DETAIL_TEMPLATE)
        elif "/sub/download" in path or "/sub/file" in path:
            self._send_bytes(SRT_BYTES, "application/octet-stream", "Movie.2019.WEBDL-1080p.chs.ass")
        else:
            self.send_response(404)
            self.end_headers()

    def _route_shooter(self, path, query):
        if path.endswith("/api/subtitle.php"):
            if self._empty(query):
                self._send_json([])
            else:
                self._send_json_selfrewritten(SHOOTER_TEMPLATE)
        elif "/file" in path:
            self._send_bytes(SRT_BYTES, "application/octet-stream", "file.srt")
        else:
            self.send_response(404)
            self.end_headers()


def main():
    parser = argparse.ArgumentParser(description="Lingarr fake subtitle-source server")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8799)
    args = parser.parse_args()

    server = ThreadingHTTPServer((args.host, args.port), FakeSourcesHandler)
    base = "http://%s:%d" % (args.host, args.port)
    print("Lingarr fake subtitle sources listening on %s" % base)
    print("Base-URL overrides to set in Lingarr settings:")
    for suffix in [
        "opensubtitles/api/v1",
        "subsource/api",
        "subdl",
        "gestdown",
        "podnapisi",
        "assrt/v1",
        "shooter",
    ]:
        print("  http://%s/%s" % (f"{args.host}:{args.port}", suffix))
    print("Fault injection: append ?_fault=429|500|empty|timeout (with _retry_after/_delay)")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
