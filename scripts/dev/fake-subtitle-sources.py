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
    subtitle_subsource_base_url     = http://127.0.0.1:8799/subsource
    subtitle_subdl_base_url         = http://127.0.0.1:8799/subdl
    subtitle_gestdown_base_url      = http://127.0.0.1:8799/gestdown
    subtitle_xunlei_base_url        = http://127.0.0.1:8799/xunlei
    subtitle_assrt_base_url         = http://127.0.0.1:8799/assrt/v1
    subtitle_shooter_base_url       = http://127.0.0.1:8799/shooter

Credentials: any non-empty value passes — opensubtitles/assrt/subsource
accept "test-key"/"test-token"; empty credentials still fail before the
request is sent (provider-side check), which is part of the contract under
test. The fake does NOT enforce the subsource api_key (any value, absent
included, gets the normal payloads) — append require_auth=1 to any
/subsource route to make a keyless request answer 401 instead.

Fault injection (any endpoint, query parameter controlled)
----------------------------------------------------------
    ?_fault=429            -> HTTP 429 + Retry-After: 3600 (or _retry_after=N)
    ?_fault=500            -> HTTP 500 (cooldown bucket check)
    ?_fault=empty          -> HTTP 200 with the source's empty-result body
    ?_fault=timeout        -> sleep _delay seconds (default 30, > the 10s
                              per-source timeout) then answer 200

Example: curl 'http://127.0.0.1:8799/gestdown/subtitles/get/x/1/2/en?_fault=429'

Xunlei response shaping (/xunlei/subxl/{hash}.json)
---------------------------------------------------
Besides ?_fault=empty (empty sublist), the xunlei route switches its body on
query parameter or hash literals so the parser's tolerance cases can be
driven from a hash-only client (the provider has no keyword or parameter
surface to attach query strings to):

    ?_sublist=empty   (or hash containing "EMPTY")   -> {"sublist": []}
    ?_sublist=garbage (or hash containing "GARBAGE") -> truncated, unparseable JSON
    default                                            -> the normal mixed sublist
                       (简体 / 繁体 / 简体&英语 / unknown-language /
                        GBK-mangled entries + percent-encoded sname, mirroring
                        the xunlei fixture)

The real endpoint answers unknown hashes with a hot-list fallback, so the
normal body for any hash (including the 40-zero probe of TestConnection) is
faithful behaviour.
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

# Mirrors Lingarr.Server.Tests/Fixtures/Subtitles/subsource/movies-search.json
# (v1 API, R4: GET /api/v1/movies/search — searchType=imdb&q=|imdb=).
SUBSOURCE_MOVIES_SEARCH = {
    "success": True,
    "data": [
        {"movieId": "sub-movie-1", "title": "Other Movie", "releaseYear": 2019, "alternateTitle": None},
        {"movieId": "sub-movie-2", "title": "Movie", "releaseYear": 2019, "alternateTitle": "Movie: An Alternate Retitling"},
    ],
}

# Mirrors .../subsource/subtitles.json (v1 API; English entry only — the
# cross-language drop case lives in the unit fixture).
SUBSOURCE_SUBTITLES = {
    "success": True,
    "data": [
        {
            "subtitleId": "sub-sub-1",
            "language": "English",
            "releaseInfo": ["Movie.2019.WEBDL-1080p"],
            "foreignParts": False,
            "hearingImpaired": False,
            "uploaderId": "sub-user-1",
            "contributors": [{"id": "sub-user-1", "displayname": "fixture-author"}],
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

# Mirrors .../gestdown/shows-search.json (R4: path-segment search route,
# {"shows":[…]} wrapper with id keys — the exact-name hit is NOT first).
GESTDOWN_SHOWS = {
    "shows": [
        {"id": "d2b7db2b-4d3b-4a72-ba0f-23f2c5a1a123", "name": "Other Show", "nbSeasons": 1},
        {"id": "e3c8ec3c-5e4c-5b83-cb1f-34a3d6b2b234", "name": "Show", "nbSeasons": 12},
    ]
}

GESTDOWN_SUBTITLES = {
    "episode": {"season": 1, "episode": 2, "title": "Episode Two"},
    "subtitles": [
        {"uuid": "sub-uuid-1", "version": "720p.WEB-DL", "language": "en"},
        {"uuid": "sub-uuid-2", "version": "1080p.WEB-DL", "language": "en"},
    ],
}

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

# Mirrors Lingarr.Server.Tests/Fixtures/Subtitles/xunlei/search.json: the
# observed language vocabulary (including the garbage entries the provider
# must drop) plus the percent-encoded sname shapes. surl placeholders are
# rewritten per request (see _send_json_selfrewritten).
XUNLEI_TEMPLATE = {
    "sublist": [
        {
            "scid": "63001",
            "sname": "Movie.2019.WEBDL-1080p.chs.srt",
            "language": "简体",
            "rate": "10",
            "svote": 3,
            "surl": "http://SELF/xunlei/file.srt",
            "roffset": 0,
        },
        {
            "scid": "63002",
            "sname": "%E7%94%B5%E5%BD%B1%3A%5CMovie.2019.cht.ass",
            "language": "繁体",
            "rate": "8",
            "svote": 1,
            "surl": "http://SELF/xunlei/file.srt",
            "roffset": 0,
        },
        {
            "scid": "63003",
            "sname": "Movie.2019.WEBDL-1080p.chs&eng.srt",
            "language": "简体&英语",
            "rate": "10",
            "svote": 5,
            "surl": "http://SELF/xunlei/file.srt",
            "roffset": 0,
        },
        {
            "scid": "63005",
            "sname": "Movie.2019.gbk-mangled.srt",
            "language": "\ufffd体",
            "rate": "1",
            "svote": 0,
            "surl": "http://SELF/xunlei/file.srt",
            "roffset": 0,
        },
        {
            "scid": "63006",
            "sname": "Movie.2019.unknown-lang.srt",
            "language": "未知语言",
            "rate": "1",
            "svote": 0,
            "surl": "http://SELF/xunlei/file.srt",
            "roffset": 0,
        },
    ]
}

XUNLEI_EMPTY = {"sublist": []}

# Deliberately truncated mid-string: the malformed-JSON tolerance case.
XUNLEI_GARBAGE = b'{"sublist":[{"scid":"63001","sname":"truncated mid-'

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
                self._route_subsource(path, query)
            elif path.startswith("/subdl/"):
                self._route_subdl(path, query)
            elif path.startswith("/gestdown/"):
                self._route_gestdown(path, query)
            elif path.startswith("/assrt/"):
                self._route_assrt(path, query)
            elif path.startswith("/xunlei/"):
                self._route_xunlei(path, query)
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

    def _route_subsource(self, path, query):
        # v1 API (R4 hotfix): GET /api/v1/movies/search (searchType=imdb|text
        # with imdb=/q= — both accepted, answers are shaped by _fault only),
        # GET /api/v1/subtitles (api_key/movieId/language/seasonNumber/
        # episodeNumber ride the query unvalidated), GET /api/v1/subtitles/
        # {id}/download (raw srt bytes — no JSON wrapper).
        if query.get("require_auth", ["0"])[0] == "1" and not (query.get("api_key") or [""])[0]:
            self._send_json({"success": False, "message": "api_key required"}, status=401)
            return
        if path.endswith("/api/v1/movies/search"):
            if self._empty(query):
                self._send_json({"success": True, "data": []})
            else:
                self._send_json(SUBSOURCE_MOVIES_SEARCH)
        elif re.search(r"/api/v1/subtitles/[^/]+/download$", path):
            self._send_bytes(SRT_BYTES, "application/octet-stream", "Movie.2019.WEBDL-1080p.en.srt")
        elif path.endswith("/api/v1/subtitles"):
            if self._empty(query):
                self._send_json({"success": True, "data": []})
            else:
                self._send_json(SUBSOURCE_SUBTITLES)
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
        if "/application/info" in path:
            # R4b hotfix: the test-connection probe — a show-search probe
            # would 404 for any probe term that matches no show.
            self._send_json({"applicationVersion": "fake-1.0.0"})
        elif "/shows/search/" in path:
            # Path-segment form (R4 hotfix): /shows/search/{title} — the
            # title rides in the path; the old ?search= query parameter was
            # retired upstream.
            if self._empty(query):
                self._send_json({"shows": []})
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
        if path.endswith("/api/subapi.php"):
            # R4 hotfix: the endpoint was renamed upstream (subtitle.php →
            # subapi.php); filehash/pathinfo/format/lang ride the query.
            if self._empty(query):
                self._send_json([])
            else:
                self._send_json_selfrewritten(SHOOTER_TEMPLATE)
        elif "/file" in path:
            self._send_bytes(SRT_BYTES, "application/octet-stream", "file.srt")
        else:
            self.send_response(404)
            self.end_headers()

    def _route_xunlei(self, path, query):
        # GET /subxl/{hash}.json — hash-only lookup, no parameters upstream.
        # Response shaping switches on ?_sublist= or hash literals (see the
        # module docstring): the provider cannot attach query strings, so the
        # hash-literal forms exist for curl-driven rounds and the default
        # body mirrors the real endpoint's unknown-hash hot-list fallback.
        match = re.search(r"/subxl/([^/.]+)\.json$", path)
        if match:
            hash_name = match.group(1)
            shape = query.get("_sublist", [None])[0]
            if self._empty(query) or shape == "empty" or "EMPTY" in hash_name.upper():
                self._send_json(XUNLEI_EMPTY)
            elif shape == "garbage" or "GARBAGE" in hash_name.upper():
                self._send_bytes(XUNLEI_GARBAGE, "application/json")
            else:
                self._send_json_selfrewritten(XUNLEI_TEMPLATE)
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
        "subsource",
        "subdl",
        "gestdown",
        "xunlei",
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
