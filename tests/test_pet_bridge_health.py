"""Offline bridge health regressions; synthetic local servers, no chat requests.

Run: <Python> -m unittest discover -s tests -p test_pet_bridge_health.py -v
SEEIN_PET_BRIDGE_SOURCE may point at a staged bridge script on another machine.
"""
import concurrent.futures
import contextlib
import importlib.util
import json
import os
from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import sys
import tempfile
import threading
import time
import types
import unittest
from unittest import mock
import urllib.request


MAINTAINED_SOURCE = Path(__file__).resolve().parents[1] / "tools/dafeiyu/pet-bridge.py"
SOURCE = Path(os.environ.get("SEEIN_PET_BRIDGE_SOURCE", str(MAINTAINED_SOURCE) if MAINTAINED_SOURCE.is_file()
                            else r"C:\dafeiyu\tools\dafeiyu_pet_bridge.py"))


@contextlib.contextmanager
def serve(handler):
    class Server(ThreadingHTTPServer):
        daemon_threads = True
        request_queue_size = 32
    server = Server(("127.0.0.1", 0), handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield server, f"http://127.0.0.1:{server.server_port}"
    finally:
        server.shutdown()
        server.server_close()
        thread.join(2)


def get_json(url, timeout=1):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(url, timeout=timeout) as response:
        return json.loads(response.read())


class FakeBrain(BaseHTTPRequestHandler):
    posts = 0
    requests = []
    status = 200
    payload = b'{"status":"ok"}'
    entered = None
    release = None

    def log_message(self, *_):
        pass

    def do_GET(self):
        type(self).requests.append(self.path.split("?", 1)[0])
        if self.entered:
            self.entered.set()
        if self.release:
            self.release.wait(2)
        data = self.payload
        self.send_response(self.status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self):
        type(self).posts += 1
        self.send_error(405, "No chat or writes in this fixture")


class BridgeHealthTests(unittest.TestCase):
    def setUp(self):
        self.backend = types.ModuleType("backend_state")
        self.backend.resolve = mock.Mock(side_effect=AssertionError("Unexpected backend probe"))
        spec = importlib.util.spec_from_file_location("bridge_health_fixture", SOURCE)
        self.bridge = importlib.util.module_from_spec(spec)
        with mock.patch.dict(sys.modules, {"backend_state": self.backend}):
            spec.loader.exec_module(self.bridge)
        FakeBrain.posts = 0
        FakeBrain.requests = []
        FakeBrain.status = 200
        FakeBrain.payload = b'{"status":"ok"}'
        FakeBrain.entered = FakeBrain.release = None

    def tearDown(self):
        self.assertEqual(0, FakeBrain.posts, "Tests must never submit chat")
        self.assertTrue(all(p == "/api/v1/chat/sessions" for p in FakeBrain.requests))

    def cfg(self, brain):
        cfg = self.bridge.Cfg()
        cfg.directory = "synthetic-directory-no-private-files"
        cfg.astrbot = brain
        cfg.owner = "123456"
        cfg.key = "synthetic-health-key"
        cfg.token = "synthetic-bridge-token"
        cfg.port = 0
        cfg.timeout = 1
        cfg.session = "fixture-never-used-for-chat"
        cfg.stream = False
        return cfg

    def wait_refresh(self, cfg):
        limit = time.monotonic() + 2
        while cfg._backend_refreshing and time.monotonic() < limit:
            time.sleep(.005)
        self.assertFalse(cfg._backend_refreshing)

    def test_deep_health_is_responsive_during_slow_single_flight_probe(self):
        gate, entered = threading.Event(), threading.Event()
        def slow_probe(_):
            entered.set()
            gate.wait(3)
            return {"url": brain}
        with serve(FakeBrain) as (_, brain):
            cfg = self.cfg(brain)
            self.backend.resolve.side_effect = slow_probe
            class Handler(self.bridge.Handler):
                pass
            Handler.cfg, Handler.log = cfg, lambda *_: None
            try:
                with serve(Handler) as (server, base):
                    cfg.port = server.server_port
                    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
                        started = time.monotonic()
                        results = list(pool.map(lambda _: get_json(base + "/health?deep=1"), range(6)))
                    self.assertLess(time.monotonic() - started, 1.5)
                    self.assertTrue(entered.is_set())
                    self.assertTrue(all(row["ok"] and row["astrbot"] == "ok" for row in results))
                    self.assertEqual(1, self.backend.resolve.call_count)
            finally:
                gate.set()
                self.wait_refresh(cfg)

    def test_plain_health_is_independent_of_slow_upstream_check(self):
        entered, release = threading.Event(), threading.Event()
        FakeBrain.entered, FakeBrain.release = entered, release
        with serve(FakeBrain) as (_, brain):
            cfg = self.cfg(brain)
            cfg.fixed_astrbot = brain
            class Handler(self.bridge.Handler):
                pass
            Handler.cfg, Handler.log = cfg, lambda *_: None
            with serve(Handler) as (_, base):
                with concurrent.futures.ThreadPoolExecutor() as pool:
                    pending = pool.submit(get_json, base + "/health?deep=1", 3)
                    try:
                        self.assertTrue(entered.wait(1))
                        started = time.monotonic()
                        self.assertTrue(get_json(base + "/health")["ok"])
                        self.assertLess(time.monotonic() - started, .8)
                    finally:
                        release.set()
                    self.assertEqual("ok", pending.result()["astrbot"])

    def test_background_probe_updates_url_and_retains_it_after_failure(self):
        cfg = self.cfg("http://127.0.0.1:10001")
        self.backend.resolve.side_effect = None
        self.backend.resolve.return_value = {"url": "http://127.0.0.1:10002/"}
        cfg.astrbot
        self.wait_refresh(cfg)
        self.assertEqual("http://127.0.0.1:10002", cfg.astrbot)
        self.backend.resolve.side_effect = RuntimeError("Synthetic resolver failure")
        with cfg._backend_lock:
            cfg._backend_cache = (0, cfg._backend_cache[1])
        cfg.astrbot
        self.wait_refresh(cfg)
        self.assertEqual("http://127.0.0.1:10002", cfg.astrbot)
        self.assertEqual(2, self.backend.resolve.call_count)
        # An error is cached too, avoiding another scan for every incoming GET.
        [cfg.astrbot for _ in range(20)]
        self.assertEqual(2, self.backend.resolve.call_count)

    def test_config_and_url_errors_are_health_failures_not_handler_exceptions(self):
        class BadCfg:
            owner = "123456"
            @property
            def astrbot(self):
                raise RuntimeError("Synthetic config read failure")
        self.assertEqual((False, "astrbot-down"), self.bridge.check_astrbot(BadCfg()))
        cfg = self.cfg("not-a-url")
        cfg.fixed_astrbot = "not-a-url"
        self.assertEqual((False, "astrbot-down"), self.bridge.check_astrbot(cfg))

    def test_health_http_errors_preserve_key_and_upstream_distinction(self):
        with serve(FakeBrain) as (_, brain):
            cfg = self.cfg(brain)
            cfg.fixed_astrbot = brain
            FakeBrain.status = 401
            self.assertEqual((False, "key"), self.bridge.check_astrbot(cfg))
            FakeBrain.status = 503
            self.assertEqual((False, "http-503"), self.bridge.check_astrbot(cfg))
            FakeBrain.status = 200
            FakeBrain.payload = b'{"status":"error","message":"temporary backend failure"}'
            self.assertEqual((False, "astrbot-error"), self.bridge.check_astrbot(cfg))

    def test_loopback_opens_without_proxy_external_urls_keep_default_opener(self):
        with serve(FakeBrain) as (_, brain):
            with mock.patch.object(urllib.request, "urlopen", side_effect=AssertionError("Default proxy opener must not handle loopback")):
                with self.bridge.open_url(brain + "/api/v1/chat/sessions", timeout=1) as response:
                    self.assertEqual(200, response.status)
        sentinel = object()
        with mock.patch.object(urllib.request, "urlopen", return_value=sentinel) as default:
            request = urllib.request.Request("https://fixture.invalid/api")
            self.assertIs(sentinel, self.bridge.open_url(request, timeout=7))
            default.assert_called_once_with(request, timeout=7)

    def test_startup_bootstraps_saved_url_without_process_or_tcp_scan(self):
        with tempfile.TemporaryDirectory() as folder:
            private = Path(folder) / "private"
            private.mkdir()
            for name, value in {"pet-key.txt": "synthetic-key", "owner-qq.txt": "123456",
                                "pet-bridge-token.txt": "synthetic-token", "backend.txt": "url: http://127.0.0.1:10001\n"}.items():
                (private / name).write_text(value, encoding="utf-8")
            args = types.SimpleNamespace(dir=folder, key_file="", owner_file="", token_file="", astrbot="",
                                         port=26199, session="fixture", timeout=1, stream=False)
            cfg = self.bridge.load_cfg(args)
            self.assertEqual("http://127.0.0.1:10001", cfg._astrbot)
            self.backend.resolve.assert_not_called()


if __name__ == "__main__":
    unittest.main()
